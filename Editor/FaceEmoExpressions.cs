using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // FaceEmo stores registered patterns separately from the descriptor FX.
    // Read that authored data; do not run build plugins or modify its assets.
    internal static class FaceEmoExpressions
    {
        // FaceEmo's mode default and branch animations are alternative states
        // on FACE EMOTE PLAYER. Only DEFAULT FACE is their common underlay.
        // Read the prepared clip after NDMF has retargeted its object paths, then
        // map those paths back to the source menu's renderer identities.
        internal static void ApplyPreparedDefaultFace(GameObject prepared, VrChatExpressionMenu.Source source,
            Func<string, string> toAuthoringPath, Func<string, bool> excludedPath = null)
        {
            ApplyPreparedDefaultFace(prepared, source, path => {
                var original = toAuthoringPath?.Invoke(path) ?? (toAuthoringPath == null ? path : null);
                return original == null ? Array.Empty<string>() : new[] { original };
            }, null, excludedPath);
        }

        internal static void ApplyPreparedDefaultFace(GameObject prepared, VrChatExpressionMenu.Source source,
            PreparedExpressionBindings bindings, Func<string, bool> excludedPath = null)
        {
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            ApplyPreparedDefaultFace(prepared, source, bindings.AuthoringPaths, path => bindings.Get(path)?.Renderer, excludedPath);
        }

        private static void ApplyPreparedDefaultFace(GameObject prepared, VrChatExpressionMenu.Source source,
            Func<string, string[]> toAuthoringPaths, Func<string, SkinnedMeshRenderer> toPreparedRenderer,
            Func<string, bool> excludedPath)
        {
            var entries = source.Entries.Where(entry => entry.Error == null && entry.Id?.StartsWith("faceemo/", StringComparison.Ordinal) == true).ToArray();
            if (entries.Length == 0) return;
            var metadata = VrChatExpressionMenu.Read(prepared, new VrChatMenuImportPolicy { SkipAll = true });
            if (metadata.Controller == null) return;
            var controller = ExpressionDependencies.Controller(metadata.Controller);
            var layers = controller.layers.Where(layer => layer.name == "[ USER EDIT ] DEFAULT FACE").ToArray();
            if (layers.Length == 0) return; // Authored registrations may exist without generated FaceEmo FX.
            try
            {
                if (layers.Length != 1 || layers[0].syncedLayerIndex >= 0 || layers[0].stateMachine == null)
                    throw new InvalidOperationException("FaceEmoの共通DEFAULT FACEレイヤーを一意に取得できません。");
                var states = layers[0].stateMachine.states.Where(child => child.state != null && child.state.name == "DEFAULT").ToArray();
                if (states.Length != 1 || !(states[0].state.motion is AnimationClip clip))
                    throw new InvalidOperationException("FaceEmoの共通DEFAULT FACEアニメーションを取得できません。");
                var replacements = ExpressionDependencies.Overrides(metadata.Controller);
                if (replacements.TryGetValue(clip, out var replacement)) clip = replacement;
                var underlay = new List<VrChatExpressionMenu.MorphValue>();
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (excludedPath?.Invoke(binding.path) == true || binding.type != typeof(SkinnedMeshRenderer) ||
                        !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                    var curve = VrChatGestureExpressions.ReadCurve(AnimationUtility.GetEditorCurve(clip, binding));
                    curve.Range(out var minimum, out var maximum);
                    if (minimum != maximum)
                        throw new InvalidOperationException("FaceEmoの共通DEFAULT FACEが時間で変化します: " + binding.path + " / " + binding.propertyName);
                    var shape = binding.propertyName.Substring("blendShape.".Length);
                    underlay.Add(new VrChatExpressionMenu.MorphValue { Path = binding.path, Shape = shape, Weight = (float)minimum });
                }
                ComposeCommonValues(prepared, entries, underlay, toAuthoringPaths, toPreparedRenderer);
            }
            catch (InvalidOperationException error)
            {
                foreach (var entry in entries) entry.Error = error.Message;
            }
        }

        // Normalize the prepared common clip by final channel before overlay.
        // Explicit branch channels win across every authoring alias; defaults
        // never inherit the alternative mode state's pose. Validate all plans
        // before publishing any values into the source menu.
        internal static void ComposeCommonValues(GameObject prepared, VrChatExpressionMenu.Source source,
            IEnumerable<VrChatExpressionMenu.MorphValue> underlay, PreparedExpressionBindings bindings)
        {
            var entries = source.Entries.Where(entry => entry.Error == null && entry.Id?.StartsWith("faceemo/", StringComparison.Ordinal) == true).ToArray();
            ComposeCommonValues(prepared, entries, underlay, bindings.AuthoringPaths, path => bindings.Get(path)?.Renderer);
        }

        private sealed class BranchChannel
        {
            internal SkinnedMeshRenderer Renderer;
            internal string Shape;
            internal float Weight;
            internal bool HasValue;
            internal VRVlog.Expressions.ExpressionAnimationData.Curve Curve;
        }

        private static void ComposeCommonValues(GameObject prepared, VrChatExpressionMenu.Entry[] entries,
            IEnumerable<VrChatExpressionMenu.MorphValue> underlay, Func<string, string[]> toAuthoringPaths,
            Func<string, SkinnedMeshRenderer> toPreparedRenderer)
        {
            var origins = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.Ordinal);
            var defaults = new Dictionary<(SkinnedMeshRenderer Renderer, string Shape), (float Weight, HashSet<string> Paths)>();
            InvalidOperationException Conflict(SkinnedMeshRenderer renderer, string shape) => new InvalidOperationException(
                "FaceEmoの統合された表情設定が一致しません: " + AnimationUtility.CalculateTransformPath(renderer.transform, prepared.transform) + " / " + shape);
            foreach (var value in underlay)
            {
                var renderer = VrChatExpressionSampler.FindRenderer(prepared, value.Path);
                var paths = toAuthoringPaths(value.Path);
                if (paths == null || paths.Length == 0)
                    throw new InvalidOperationException("FaceEmoの共通DEFAULT FACEの元Rendererを特定できません: " + value.Path);
                var key = (renderer, value.Shape);
                if (defaults.TryGetValue(key, out var found))
                {
                    if (!found.Weight.Equals(value.Weight)) throw Conflict(renderer, value.Shape);
                    found.Paths.UnionWith(paths);
                }
                else defaults.Add(key, (value.Weight, new HashSet<string>(paths, StringComparer.Ordinal)));
                foreach (var path in paths)
                {
                    if (origins.TryGetValue(path, out var previous) && previous != renderer) throw Conflict(renderer, value.Shape);
                    origins[path] = renderer;
                }
            }
            var plans = new List<(VrChatExpressionMenu.Entry Entry, List<VrChatExpressionMenu.MorphValue> Values)>();
            foreach (var entry in entries)
            {
                var channels = new Dictionary<(string Path, string Shape), BranchChannel>();
                BranchChannel Channel(string path, string shape)
                {
                    var key = (path, shape);
                    if (channels.TryGetValue(key, out var known)) return known;
                    var renderer = toPreparedRenderer?.Invoke(path);
                    if (renderer == null) origins.TryGetValue(path, out renderer);
                    var channel = new BranchChannel { Renderer = renderer, Shape = shape };
                    channels.Add(key, channel); return channel;
                }
                foreach (var value in entry.Values)
                {
                    var channel = Channel(value.Path, value.Shape);
                    if (channel.HasValue && !channel.Weight.Equals(value.Weight) && channel.Renderer != null) throw Conflict(channel.Renderer, channel.Shape);
                    channel.HasValue = true; channel.Weight = value.Weight;
                }
                foreach (var animated in entry.Animation)
                {
                    var channel = Channel(animated.Path, animated.Shape);
                    if (channel.Curve != null && !VrChatExpressionBaker.SameCurve(channel.Curve, animated.Curve) && channel.Renderer != null)
                        throw Conflict(channel.Renderer, channel.Shape);
                    channel.Curve = animated.Curve;
                }
                foreach (var channel in channels.Values.Where(channel => channel.Renderer != null && channel.Curve != null))
                {
                    var initial = (float)channel.Curve.Evaluate(0);
                    if (channel.HasValue && !channel.Weight.Equals(initial)) throw Conflict(channel.Renderer, channel.Shape);
                    channel.Weight = initial;
                }
                var explicitChannels = new HashSet<(SkinnedMeshRenderer Renderer, string Shape)>();
                foreach (var group in channels.Values.Where(channel => channel.Renderer != null).GroupBy(channel => (channel.Renderer, channel.Shape)))
                {
                    var first = group.First();
                    foreach (var other in group.Skip(1))
                        if (!first.Weight.Equals(other.Weight) || !(VrChatExpressionBaker.SameCurve(first.Curve, other.Curve) ||
                            VrChatExpressionBaker.ConstantAt(first.Curve, first.Weight) && VrChatExpressionBaker.ConstantAt(other.Curve, other.Weight)))
                            throw Conflict(first.Renderer, first.Shape);
                    explicitChannels.Add(group.Key);
                }
                var values = new List<VrChatExpressionMenu.MorphValue>(entry.Values);
                foreach (var pair in defaults.Where(pair => !explicitChannels.Contains(pair.Key)))
                    foreach (var path in pair.Value.Paths.OrderBy(path => path, StringComparer.Ordinal))
                        values.Add(new VrChatExpressionMenu.MorphValue { Path = path, Shape = pair.Key.Shape, Weight = pair.Value.Weight });
                plans.Add((entry, values));
            }
            foreach (var plan in plans)
            {
                plan.Entry.Values.Clear();
                plan.Entry.Values.AddRange(plan.Values.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Shape, StringComparer.Ordinal));
            }
        }

        internal static void Add(GameObject avatar, VrChatExpressionMenu.Source source, Func<string, bool> excludedPath = null)
        {
            var serial = 0;
            var repositories = new HashSet<Component>();
            foreach (var repository in avatar.GetComponentsInChildren<Component>().Where(IsRepository))
            {
                var launcher = repository.GetComponents<Component>().FirstOrDefault(IsLauncher);
                // A pet or another embedded avatar may have its own FaceEmo
                // configuration. Honor its target even inside this hierarchy.
                if (launcher == null || TargetsAvatar(avatar, Member(launcher, "AV3Setting"))) repositories.Add(repository);
            }
            // FaceEmo normally creates a separate scene object. Its settings,
            // not its position in the hierarchy or its name, identify the avatar.
            // FindObjectsOfType excludes inactive restoration checkpoints and
            // project assets; neither is the user's current FaceEmo setup.
            foreach (var launcher in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (!IsLauncher(launcher) || !TargetsAvatar(avatar, Member(launcher, "AV3Setting"))) continue;
                foreach (var repository in launcher.GetComponents<Component>().Where(IsRepository))
                    repositories.Add(repository);
            }
            foreach (var component in repositories)
            {
                var menu = Member(component, "SerializableMenu");
                if (menu == null) { source.Messages.Add("FaceEmo: 保存済みの表情メニューがありません。"); continue; }
                if (Member(menu, "Registered") == null)
                {
                    source.Messages.Add("FaceEmo: 登録済みパターンのデータを読み取れませんでした。");
                    continue;
                }
                ReadRegistered(avatar, Member(menu, "Registered"), "FaceEmo", source, ref serial, new HashSet<object>(), 0, excludedPath);
            }
        }

        private static bool IsRepository(Component component) => component != null &&
            component.GetType().FullName == "Suzuryg.FaceEmo.Components.Data.MenuRepositoryComponent";

        private static bool IsLauncher(Component component) => component != null &&
            component.GetType().FullName == "Suzuryg.FaceEmo.Components.FaceEmoLauncherComponent";

        internal static bool TargetsAvatar(GameObject avatar, object settings)
        {
            bool Matches(object target) => target is Component component && component != null && component.gameObject == avatar;
            return Matches(Member(settings, "TargetAvatar")) || Items(Member(settings, "SubTargetAvatars")).Any(Matches);
        }

        internal static void ReadRegistered(GameObject avatar, object list, string prefix, VrChatExpressionMenu.Source source,
            ref int serial, HashSet<object> visited, int depth, Func<string, bool> excludedPath = null)
        {
            if (list == null) return;
            if (depth > 16 || !visited.Add(list)) throw new InvalidOperationException("FaceEmoのグループ参照が循環しているか深すぎます。");
            try
            {
                foreach (var item in OrderedItems(list))
                {
                    if (item.IsGroup)
                    {
                        ReadRegistered(avatar, item.Value, prefix + " / " + (Member(item.Value, "DisplayName") as string ?? "グループ"),
                            source, ref serial, visited, depth + 1, excludedPath);
                        continue;
                    }
                    var mode = item.Value;
                    var defaultAnimation = Member(mode, "ChangeDefaultFace") is bool enabled && enabled ? Member(mode, "Animation") : null;
                    var displayName = Member(mode, "DisplayName") as string;
                    if (defaultAnimation != null && Member(mode, "UseAnimationNameAsDisplayName") is bool useClip && useClip)
                    {
                        var animationName = Resolve(Member(mode, "Animation"))?.name;
                        if (!string.IsNullOrEmpty(animationName)) displayName = animationName;
                    }
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = "表情パターン";
                    var path = prefix + " / " + displayName;
                    if (defaultAnimation != null) AddClip(avatar, defaultAnimation, path + " / デフォルト", source, ref serial, excludedPath);
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
                            AddClip(avatar, animation, path + " / " + branchIndex + " / " + label, source, ref serial, excludedPath);
                        }
                    }
                }
            }
            finally { visited.Remove(list); }
        }

        private static IEnumerable<(object Value, bool IsGroup)> OrderedItems(object list)
        {
            var modes = new Queue<object>(Items(Member(list, "Modes")));
            var groups = new Queue<object>(Items(Member(list, "Groups")));
            var types = Member(list, "Types");
            // Keep compatibility with data from earlier integrations that did
            // not expose ordering. Current FaceEmo serializes interleaved Types.
            if (types == null)
            {
                foreach (var mode in modes) yield return (mode, false);
                foreach (var group in groups) yield return (group, true);
                yield break;
            }
            foreach (var type in Items(types))
            {
                var name = type?.ToString();
                var isGroup = name == "Group" || name == "1";
                if (!isGroup && name != "Mode" && name != "0")
                    throw new InvalidOperationException("FaceEmoのメニュー項目の種類を読み取れませんでした。");
                var queue = isGroup ? groups : modes;
                if (queue.Count == 0) throw new InvalidOperationException("FaceEmoのメニュー順序と保存済み項目が一致しません。");
                var item = queue.Dequeue();
                if (item == null) throw new InvalidOperationException("FaceEmoのメニュー項目への参照がありません。");
                yield return (item, isGroup);
            }
            if (modes.Count != 0 || groups.Count != 0)
                throw new InvalidOperationException("FaceEmoのメニュー順序と保存済み項目が一致しません。");
        }

        private static void AddClip(GameObject avatar, object animation, string path, VrChatExpressionMenu.Source source, ref int serial, Func<string, bool> excludedPath)
        {
            if (source.Entries.Count >= 512) throw new InvalidOperationException("表情候補が512件を超えています。");
            var entry = new VrChatExpressionMenu.Entry { Id = "faceemo/" + serial++, Name = path };
            try
            {
                var clip = Resolve(animation);
                if (clip == null) throw new InvalidOperationException("FaceEmoに登録されたアニメーションGUIDを解決できません。");
                entry.Name = path + " / " + clip.name;
                VrChatGestureExpressions.ReadClip(avatar, clip, entry, excludedPath);
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
            if (value == null || value is UnityEngine.Object obj && obj == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return value.GetType().GetField(name, flags)?.GetValue(value) ?? value.GetType().GetProperty(name, flags)?.GetValue(value);
        }
        private static IEnumerable<object> Items(object value) => value is IEnumerable items ? items.Cast<object>() : Enumerable.Empty<object>();
    }
}
