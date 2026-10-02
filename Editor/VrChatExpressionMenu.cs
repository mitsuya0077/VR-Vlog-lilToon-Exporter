using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // An export recipe, never an edit to the descriptor or shared menu asset.
    // Routes use control indices so repeated names/shared assets remain distinct.
    internal sealed class VrChatMenuImportPolicy
    {
        internal bool SkipAll;
        internal object ExpectedRoot;
        internal readonly HashSet<string> ExcludedBranches = new HashSet<string>(StringComparer.Ordinal);
        internal bool Excludes(string route) => ExcludedBranches.Contains(route);
    }

    internal sealed class VrChatMenuImportSummary
    {
        internal sealed class Branch
        {
            internal string Path, Label;
            internal int CandidateCount;
            internal bool IsComplete = true;
        }
        internal object Root;
        internal int CandidateCount, VisitedMenus, VisitedControls;
        internal bool IsComplete = true;
        internal bool BudgetStopped;
        internal string LimitReason;
        internal readonly List<Branch> Branches = new List<Branch>();
    }

    internal sealed class VrChatMenuImportLimitException : InvalidOperationException
    {
        internal readonly VrChatMenuImportSummary Summary;
        internal VrChatMenuImportLimitException(VrChatMenuImportSummary summary)
            : base("VRChatメニューの取り込み上限を超えています。取り込まない範囲を選ぶか、メニュー由来の表情・ポーズの取り込みを省略して再試行してください。")
        { Summary = summary; }
    }

    internal sealed class VrChatMenuImportPolicyException : InvalidOperationException
    {
        internal VrChatMenuImportPolicyException()
            : base("ビルド処理でVRChatメニューが変更されたため、選んだ枝を確認できません。全省略を選ぶか、メニューを確認して再検査してください。") { }
    }

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
            internal string ControlType, ControlParameter;
            internal double Duration = 0;
            internal bool Loop = false;
            internal readonly List<AnimatedMorph> Animation = new List<AnimatedMorph>();
            internal readonly Dictionary<string, float> Parameters = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly List<MorphValue> Values = new List<MorphValue>();
            // A sampled menu cannot supply a weight for a morph which does not
            // exist yet. Recheck these references after authoring preparation.
            internal readonly List<MorphValue> Unevaluated = new List<MorphValue>();
            internal readonly List<string> Messages = new List<string>();
        }

        internal sealed class AnimatedMorph
        {
            internal string Path = "", Shape = "";
            internal VRVlog.Expressions.ExpressionAnimationData.Curve Curve = null;
        }

        internal sealed class Source
        {
            internal RuntimeAnimatorController Controller;
            internal readonly Dictionary<string, float> Defaults = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly HashSet<string> ExpressionParameters = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<RuntimeAnimatorController> OtherControllers = new List<RuntimeAnimatorController>();
            internal readonly HashSet<string> ExternalParameters = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<string> Messages = new List<string>();
            internal int VisitedMenus;
            internal int VisitedControls;
        }

        internal const int MaximumCandidates = 256, MaximumMenuVisits = 512, MaximumDepth = 16, MaximumControlVisits = 8192;

        internal static object Root(GameObject avatar)
        {
            var descriptor = Descriptor(avatar);
            return descriptor != null && Member(descriptor, "customExpressions") is bool enabled && enabled
                ? Member(descriptor, "expressionsMenu") : null;
        }
        static Component Descriptor(GameObject avatar) => avatar.GetComponents<Component>().FirstOrDefault(c => c != null &&
            c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");

        internal static Source ReadMenu(object menu, VrChatMenuImportPolicy policy = null)
        {
            var result = new Source();
            Walk(menu, "", "", new Dictionary<string, float>(), new HashSet<object>(), result, 0, policy);
            return result;
        }

        internal static Source Read(GameObject avatar, VrChatMenuImportPolicy policy = null)
        {
            var result = new Source();
            result.Defaults["IsLocal"] = 1f;
            result.Defaults["TrackingType"] = 6f;
            var descriptor = Descriptor(avatar);
            if (descriptor == null) { result.Messages.Add("VRChat Avatar Descriptorがありません。既存のVRM表情はそのまま保存します。"); return result; }
            if (Member(descriptor, "customExpressions") is bool enabled && enabled)
                Walk(Member(descriptor, "expressionsMenu"), "", "", new Dictionary<string, float>(), new HashSet<object>(), result, 0, policy);
            else result.Messages.Add("VRChatのCustom Expressionsが無効です。FXに登録されたジェスチャー表情を確認します。");
            foreach (var parameter in Items(Member(Member(descriptor, "expressionParameters"), "parameters")))
            {
                var name = Member(parameter, "name") as string;
                if (!string.IsNullOrEmpty(name))
                {
                    result.Defaults[name] = Number(Member(parameter, "defaultValue"));
                    result.ExpressionParameters.Add(name);
                }
            }
            if (Member(descriptor, "customizeAnimationLayers") is bool custom && custom)
                foreach (var layer in Items(Member(descriptor, "baseAnimationLayers")).Concat(Items(Member(descriptor, "specialAnimationLayers"))))
                {
                    if (Member(layer, "isDefault") is bool isDefault && isDefault) continue;
                    var runtime = Member(layer, "animatorController") as RuntimeAnimatorController;
                    if (Member(layer, "type")?.ToString() == "FX") result.Controller = runtime;
                    else if (runtime != null) result.OtherControllers.Add(runtime);
                }
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var name = component.GetType().Name;
                var parameter = Member(component, "parameter") as string;
                if (string.IsNullOrEmpty(parameter)) continue;
                if (name == "VRCContactReceiver") result.ExternalParameters.Add(parameter);
                if (name == "VRCPhysBone")
                    foreach (var suffix in new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" })
                        result.ExternalParameters.Add(parameter + suffix);
            }
            if (result.Controller == null) result.Messages.Add("カスタムFX Animatorがありません。メニューの表情を解決できません。");
            return result;
        }

        internal static void Walk(object menu, string prefix, string idPrefix, Dictionary<string, float> parents,
            HashSet<object> stack, Source result, int depth, VrChatMenuImportPolicy policy = null)
        {
            if (policy?.SkipAll == true)
            {
                result.Messages.Add(Localize("選択した対策により、VRChatメニュー由来の表情・ポーズの取り込みをすべて省略しました。"));
                return;
            }
            if (policy != null && policy.ExcludedBranches.Count != 0 && !ReferenceEquals(policy.ExpectedRoot, menu))
                throw new VrChatMenuImportPolicyException();
            if (policy != null)
                foreach (var path in policy.ExcludedBranches)
                    if (!IsSubMenuPath(menu, path))
                        throw new InvalidOperationException("選んだVRChatメニューの枝を確認できません。再検査してください。");
            WalkEntries(menu, prefix, idPrefix, parents, stack, result, depth, policy, menu);
        }

        static void WalkEntries(object menu, string prefix, string idPrefix, Dictionary<string, float> parents,
            HashSet<object> stack, Source result, int depth, VrChatMenuImportPolicy policy, object root)
        {
            if (menu == null || menu is UnityEngine.Object obj && obj == null) return;
            if (++result.VisitedMenus > MaximumMenuVisits) throw Limit(root, policy, "menu-visits");
            if (depth > MaximumDepth || !stack.Add(menu)) { result.Messages.Add(prefix + ": 循環または深すぎるサブメニューを省略しました。"); return; }
            try
            {
                var index = 0;
                foreach (var control in Items(Member(menu, "controls")))
                {
                    if (++result.VisitedControls > MaximumControlVisits) throw Limit(root, policy, "control-visits");
                    var id = idPrefix + "/" + index++;
                    var label = Member(control, "name") as string;
                    if (string.IsNullOrWhiteSpace(label)) label = "名称未設定";
                    var name = string.IsNullOrEmpty(prefix) ? label : prefix + " / " + label;
                    var type = Member(control, "type")?.ToString();
                    if (type == "SubMenu" && policy?.Excludes(id) == true)
                    {
                        result.Messages.Add(string.Format(Localize("{0}: 選択した対策により、この枝の表情・ポーズの取り込みを省略しました。"), name));
                        continue;
                    }
                    // Empty submenus do not consume a candidate slot. Count a
                    // leaf only when adding it, including unsupported controls.
                    if (type != "SubMenu" && result.Entries.Count >= MaximumCandidates) throw Limit(root, policy, "candidates");
                    var values = new Dictionary<string, float>(parents, StringComparer.Ordinal);
                    var parameter = Member(Member(control, "parameter"), "name") as string;
                    if (!string.IsNullOrEmpty(parameter)) values[parameter] = Number(Member(control, "value"));
                    if (type == "SubMenu") { WalkEntries(Member(control, "subMenu"), name, id, values, stack, result, depth + 1, policy, root); continue; }
                    var entry = new Entry { Id = id + "\n" + name + "\n" + JsonDom.Serialize(values.ToDictionary(p => p.Key, p => (object)p.Value)), Name = name,
                        ControlType = type, ControlParameter = parameter };
                    foreach (var pair in values) entry.Parameters.Add(pair.Key, pair.Value);
                    if (type != "Button" && type != "Toggle") entry.Error = "連続調整（Puppet）は一つの固定表情に変換できません。";
                    else if (string.IsNullOrEmpty(parameter)) entry.Error = "操作対象のパラメーターがありません。";
                    result.Entries.Add(entry);
                }
            }
            finally { stack.Remove(menu); }
        }

        static VrChatMenuImportLimitException Limit(object root, VrChatMenuImportPolicy policy, string reason)
        {
            var summary = Discover(root, policy);
            summary.LimitReason = reason;
            return new VrChatMenuImportLimitException(summary);
        }

        // Discovery never samples an Animator, material or expression. Its own
        // budget bounds hostile or highly reused menu graphs independently of
        // the 256 candidates that may be imported. Partial counts are lower bounds.
        internal static VrChatMenuImportSummary Discover(object root, VrChatMenuImportPolicy policy = null)
        {
            var summary = new VrChatMenuImportSummary { Root = root };
            var stack = new HashSet<object>();
            var ancestors = new List<VrChatMenuImportSummary.Branch>();
            DiscoverMenu(root, "", "", 0, policy, summary, stack, ancestors);
            return summary;
        }

        static void DiscoverMenu(object menu, string prefix, string route, int depth, VrChatMenuImportPolicy policy,
            VrChatMenuImportSummary summary, HashSet<object> stack, List<VrChatMenuImportSummary.Branch> ancestors)
        {
            if (summary.BudgetStopped || menu == null || menu is UnityEngine.Object obj && obj == null) return;
            if (++summary.VisitedMenus > MaximumMenuVisits) { Truncate(summary, ancestors, "menu-visits"); return; }
            if (depth > MaximumDepth) { MarkIncomplete(summary, ancestors, "depth"); return; }
            if (!stack.Add(menu)) { MarkIncomplete(summary, ancestors, "cycle"); return; }
            try
            {
                var index = 0;
                foreach (var control in Items(Member(menu, "controls")))
                {
                    if (++summary.VisitedControls > MaximumControlVisits) { Truncate(summary, ancestors, "control-visits"); return; }
                    var path = route + "/" + index++;
                    var label = Member(control, "name") as string;
                    if (string.IsNullOrWhiteSpace(label)) label = "名称未設定";
                    var name = string.IsNullOrEmpty(prefix) ? label : prefix + " / " + label;
                    if (Member(control, "type")?.ToString() == "SubMenu")
                    {
                        if (policy?.Excludes(path) == true) continue;
                        var branch = new VrChatMenuImportSummary.Branch { Path = path, Label = name };
                        summary.Branches.Add(branch); ancestors.Add(branch);
                        DiscoverMenu(Member(control, "subMenu"), name, path, depth + 1, policy, summary, stack, ancestors);
                        ancestors.RemoveAt(ancestors.Count - 1);
                        if (summary.BudgetStopped) return;
                    }
                    else
                    {
                        summary.CandidateCount++;
                        foreach (var branch in ancestors) branch.CandidateCount++;
                    }
                }
            }
            finally { stack.Remove(menu); }
        }

        static void Truncate(VrChatMenuImportSummary summary, List<VrChatMenuImportSummary.Branch> ancestors, string reason)
        {
            summary.BudgetStopped = true;
            MarkIncomplete(summary, ancestors, reason);
            summary.LimitReason = reason;
        }

        static void MarkIncomplete(VrChatMenuImportSummary summary, List<VrChatMenuImportSummary.Branch> ancestors, string reason)
        {
            summary.IsComplete = false; summary.LimitReason = reason;
            foreach (var branch in ancestors) branch.IsComplete = false;
        }

        internal static bool IsSubMenuPath(object root, string path)
        {
            // At most 17 indices, each in 0..8191, and their '/' separators.
            // Reject forged long strings before allocating the split array.
            if (string.IsNullOrEmpty(path) || path.Length > (MaximumDepth + 1) * 5 || path[0] != '/') return false;
            var parts = path.Substring(1).Split('/');
            if (parts.Length > MaximumDepth + 1) return false;
            var current = root;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var index) || index < 0 || index >= MaximumControlVisits || index.ToString() != part) return false;
                object selected = null; var at = 0;
                foreach (var control in Items(Member(current, "controls")))
                {
                    if (at++ == index) { selected = control; break; }
                    if (at > MaximumControlVisits) return false;
                }
                if (selected == null || Member(selected, "type")?.ToString() != "SubMenu") return false;
                current = Member(selected, "subMenu");
            }
            return true;
        }

        internal static object Member(object value, string name)
        {
            if (value == null) return null;
            var type = value.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return type.GetField(name, flags)?.GetValue(value) ?? type.GetProperty(name, flags)?.GetValue(value);
        }

        static string Localize(string message)
        {
#if EXPORTER_BEHAVIOR_TESTS
            // Host traversal tests intentionally compile without Editor APIs.
            return message;
#else
            return ExporterLocalization.T(message);
#endif
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
