using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // FaceEmo stores registered patterns separately from the descriptor FX.
    // Read that authored data; do not run build plugins or modify its assets.
    internal static class FaceEmoExpressions
    {
        internal static void Add(GameObject avatar, VrChatExpressionMenu.Source source)
        {
            var serial = 0;
            foreach (var component in avatar.GetComponentsInChildren<Component>())
            {
                if (component == null || component.GetType().FullName != "Suzuryg.FaceEmo.Components.Data.MenuRepositoryComponent") continue;
                var menu = Member(component, "SerializableMenu");
                if (menu == null) { source.Messages.Add("FaceEmo: 保存済みの表情メニューがありません。"); continue; }
                if (Member(menu, "Registered") == null)
                {
                    source.Messages.Add("FaceEmo: 登録済みパターンのデータを読み取れませんでした。");
                    continue;
                }
                ReadRegistered(avatar, Member(menu, "Registered"), "FaceEmo", source, ref serial, new HashSet<object>(), 0);
            }
        }

        internal static void ReadRegistered(GameObject avatar, object list, string prefix, VrChatExpressionMenu.Source source,
            ref int serial, HashSet<object> visited, int depth)
        {
            if (list == null) return;
            if (depth > 16 || !visited.Add(list)) throw new InvalidOperationException("FaceEmoのグループ参照が循環しているか深すぎます。");
            try
            {
                foreach (var mode in Items(Member(list, "Modes")))
                {
                    var defaultAnimation = Member(mode, "ChangeDefaultFace") is bool enabled && enabled ? Member(mode, "Animation") : null;
                    var displayName = Member(mode, "DisplayName") as string;
                    if (defaultAnimation != null && Member(mode, "UseAnimationNameAsDisplayName") is bool useClip && useClip)
                    {
                        var animationName = Resolve(Member(mode, "Animation"))?.name;
                        if (!string.IsNullOrEmpty(animationName)) displayName = animationName;
                    }
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = "表情パターン";
                    var path = prefix + " / " + displayName;
                    if (defaultAnimation != null) AddClip(avatar, defaultAnimation, path + " / デフォルト", source, ref serial);
                    var branchIndex = 0;
                    foreach (var branch in Items(Member(mode, "Branches")))
                    {
                        branchIndex++;
                        // Trigger endpoints are separate authored expressions,
                        // never claimed to preserve VRChat's continuous input.
                        var variants = new List<string> { "BaseAnimation" };
                        if (Member(branch, "IsLeftTriggerUsed") is bool left && left) variants.Add("LeftHandAnimation");
                        if (Member(branch, "IsRightTriggerUsed") is bool right && right) variants.Add("RightHandAnimation");
                        if (variants.Count == 3) variants.Add("BothHandsAnimation");
                        foreach (var variant in variants)
                        {
                            var animation = Member(branch, variant);
                            if (animation == null) continue;
                            var label = variant == "BaseAnimation" ? "基本" : variant == "LeftHandAnimation" ? "左トリガー" : variant == "RightHandAnimation" ? "右トリガー" : "両トリガー";
                            AddClip(avatar, animation, path + " / " + branchIndex + " / " + label, source, ref serial);
                        }
                    }
                }
                foreach (var group in Items(Member(list, "Groups")))
                    ReadRegistered(avatar, group, prefix + " / " + (Member(group, "DisplayName") as string ?? "グループ"), source, ref serial, visited, depth + 1);
            }
            finally { visited.Remove(list); }
        }

        private static void AddClip(GameObject avatar, object animation, string path, VrChatExpressionMenu.Source source, ref int serial)
        {
            if (source.Entries.Count >= 512) throw new InvalidOperationException("表情候補が512件を超えています。");
            var entry = new VrChatExpressionMenu.Entry { Id = "faceemo/" + serial++, Name = path };
            try
            {
                var clip = Resolve(animation);
                if (clip == null) throw new InvalidOperationException("FaceEmoに登録されたアニメーションGUIDを解決できません。");
                entry.Name = path + " / " + clip.name;
                entry.Values.AddRange(VrChatGestureExpressions.ReadPose(avatar, clip));
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = VrChatGestureExpressions.ReadCurve(AnimationUtility.GetEditorCurve(clip, binding));
                    curve.Range(out var minimum, out var maximum);
                    if (minimum == maximum) continue;
                    entry.Animation.Add(new VrChatExpressionMenu.AnimatedMorph
                    {
                        Path = binding.path, Shape = binding.propertyName.Substring("blendShape.".Length), Curve = curve
                    });
                }
                if (entry.Animation.Count > 0)
                {
                    entry.Duration = clip.length;
                    entry.Loop = clip.isLooping;
                    if (entry.Duration <= 0 || entry.Duration > 600) throw new InvalidOperationException("FaceEmoの表情アニメーションは0秒より長く600秒以下である必要があります。");
                }
            }
            catch (InvalidOperationException error) { entry.Error = error.Message; }
            source.Entries.Add(entry);
        }

        private static AnimationClip Resolve(object animation)
        {
            var guid = Member(animation, "GUID") as string;
            return string.IsNullOrEmpty(guid) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guid));
        }
        private static object Member(object value, string name)
        {
            if (value == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return value.GetType().GetField(name, flags)?.GetValue(value) ?? value.GetType().GetProperty(name, flags)?.GetValue(value);
        }
        private static IEnumerable<object> Items(object value) => value is IEnumerable items ? items.Cast<object>() : Enumerable.Empty<object>();
    }
}
