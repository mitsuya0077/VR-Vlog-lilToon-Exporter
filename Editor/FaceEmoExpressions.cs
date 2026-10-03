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
        // FaceEmo's GUID clips are outside the FX controller rewritten by MA.
        // Remember their targets on our copy before Transforming moves them;
        // evaluate the clips only after the merged FX controller is available.
        internal sealed class BindingSnapshot
        {
            private readonly GameObject clone;
            private readonly Func<string, bool> originalExcludedPath;
            internal Func<string, bool> PreparedExcludedPath { get; }
            private sealed class Target
            {
                internal Component[] Matches;
                internal bool Excluded;
            }
            private readonly Dictionary<AnimationClip, Dictionary<EditorCurveBinding, Target>> targets =
                new Dictionary<AnimationClip, Dictionary<EditorCurveBinding, Target>>();

            internal BindingSnapshot(GameObject clone, Func<string, bool> excludedPath = null, Func<string, bool> preparedExcludedPath = null)
            { this.clone = clone; originalExcludedPath = excludedPath; PreparedExcludedPath = preparedExcludedPath; }

            internal void CaptureClip(AnimationClip clip)
            {
                if (clip == null || targets.ContainsKey(clip)) return;
                targets.Add(clip, AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    .ToDictionary(binding => binding, binding => new Target
                    {
                        Excluded = originalExcludedPath?.Invoke(binding.path) == true,
                        // A same-path auxiliary object with another component
                        // type is not the registered facial renderer's identity.
                        Matches = (typeof(Component).IsAssignableFrom(binding.type)
                                ? clone.GetComponentsInChildren(binding.type, true).Cast<Component>()
                                : clone.GetComponentsInChildren<Transform>(true).Cast<Component>())
                            .Where(target => AnimationUtility.CalculateTransformPath(target.transform, clone.transform) == binding.path).ToArray()
                    }));
            }

            internal void ReadClip(GameObject avatar, AnimationClip original, VrChatExpressionMenu.Entry entry)
            {
                if (avatar != clone) throw new InvalidOperationException("FaceEmoの表情参照は書き出し用コピーと一致していません。");
                if (!targets.TryGetValue(original, out var paths))
                    throw new InvalidOperationException("FaceEmoの表情アニメーションが準備前の登録内容と一致していません: " + original.name);
                EditorCurveBinding? Remap(EditorCurveBinding binding)
                {
                    if (!paths.TryGetValue(binding, out var captured))
                        throw new InvalidOperationException("FaceEmoの表情アニメーションが準備前の登録内容と一致していません: " + original.name);
                    // Deliberate omissions follow their original identity. A
                    // historical automatic path is queried only at capture;
                    // after MA reparenting the live mapped path is authoritative.
                    if (captured.Excluded) return null;
                    if (captured.Matches.Length == 0)
                        throw new InvalidOperationException("Rendererのパスを一意に解決できません: " + binding.path);
                    if (captured.Matches.Length > 1)
                        throw new InvalidOperationException("重複する階層パスの表情は取り込めません: " + binding.path);
                    if (captured.Matches.Length == 1)
                    {
                        var target = captured.Matches[0];
                        if (target == null || target.transform != clone.transform && !target.transform.IsChildOf(clone.transform))
                            throw new InvalidOperationException("衣装・体形の処理後にFaceEmoの表情対象が残っていません: " + binding.path);
                        binding.path = AnimationUtility.CalculateTransformPath(target.transform, clone.transform);
                    }
                    return PreparedExcludedPath?.Invoke(binding.path) == true ? (EditorCurveBinding?)null : binding;
                }
                var clip = UnityEngine.Object.Instantiate(original);
                clip.name = original.name;
                clip.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    // Remove first so a reparented path cannot overwrite another
                    // still-authored curve while paths are being rewritten.
                    var curves = AnimationUtility.GetCurveBindings(original)
                        .Select(binding => (Binding: Remap(binding), Curve: AnimationUtility.GetEditorCurve(original, binding)))
                        .Where(curve => curve.Binding.HasValue).ToArray();
                    var objects = AnimationUtility.GetObjectReferenceCurveBindings(original)
                        .Select(binding => (Binding: Remap(binding), Curve: AnimationUtility.GetObjectReferenceCurve(original, binding)))
                        .Where(curve => curve.Binding.HasValue).ToArray();
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip)) AnimationUtility.SetEditorCurve(clip, binding, null);
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    var seen = new HashSet<EditorCurveBinding>();
                    foreach (var curve in curves)
                    {
                        var binding = curve.Binding.Value;
                        if (!seen.Add(binding)) throw new InvalidOperationException("FaceEmoの表情参照が処理後に重複しています: " + binding.path + " / " + binding.propertyName);
                        AnimationUtility.SetEditorCurve(clip, binding, curve.Curve);
                    }
                    foreach (var curve in objects)
                    {
                        var binding = curve.Binding.Value;
                        if (!seen.Add(binding)) throw new InvalidOperationException("FaceEmoの表情参照が処理後に重複しています: " + binding.path + " / " + binding.propertyName);
                        AnimationUtility.SetObjectReferenceCurve(clip, binding, curve.Curve);
                    }
                    VrChatGestureExpressions.ReadClip(avatar, clip, entry, PreparedExcludedPath);
                    if (entry.Animation.Count > 0)
                    {
                        if (original.length <= 0 || original.length > 600)
                            throw new InvalidOperationException("表情アニメーションの長さは0秒より長く600秒以下である必要があります: " + original.name);
                        entry.Duration = original.length;
                        entry.Loop = original.isLooping;
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(clip); }
            }
        }

        internal static BindingSnapshot Capture(GameObject authoringSource, GameObject clone, Func<string, bool> excludedPath = null,
            Func<string, bool> preparedExcludedPath = null)
        {
            var snapshot = new BindingSnapshot(clone, excludedPath, preparedExcludedPath);
            ReadRepositories(authoringSource, new VrChatExpressionMenu.Source(), registered =>
                CaptureRegistered(clone, registered, snapshot));
            return snapshot;
        }

        // Shares the serialized-data route with real FaceEmo discovery so tests
        // can cover path remapping without installing its optional SDK.
        internal static BindingSnapshot CaptureRegistered(GameObject clone, object registered, BindingSnapshot snapshot = null, Func<string, bool> excludedPath = null,
            Func<string, bool> preparedExcludedPath = null)
        {
            snapshot = snapshot ?? new BindingSnapshot(clone, excludedPath, preparedExcludedPath);
            VisitRegistered(registered, "FaceEmo", new HashSet<object>(), 0,
                (animation, path) => snapshot.CaptureClip(Resolve(animation)));
            return snapshot;
        }

        internal static void Add(GameObject avatar, VrChatExpressionMenu.Source source, Func<string, bool> excludedPath = null,
            GameObject authoringSource = null, BindingSnapshot bindings = null)
        {
            var authored = authoringSource ?? avatar;
            var serial = 0;
            ReadRepositories(authored, source, registered =>
                ReadRegistered(avatar, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0, excludedPath, bindings));
        }

        private static void ReadRepositories(GameObject authored, VrChatExpressionMenu.Source source, Action<object> readRegistered)
        {
            var repositories = new HashSet<Component>();
            foreach (var repository in authored.GetComponentsInChildren<Component>().Where(IsRepository))
            {
                var launcher = repository.GetComponents<Component>().FirstOrDefault(IsLauncher);
                // A pet or another embedded avatar may have its own FaceEmo
                // configuration. Honor its target even inside this hierarchy.
                if (launcher == null || TargetsAvatar(authored, Member(launcher, "AV3Setting"))) repositories.Add(repository);
            }
            // FaceEmo normally creates a separate scene object. Its settings,
            // not its position in the hierarchy or its name, identify the avatar.
            // FindObjectsOfType excludes inactive restoration checkpoints and
            // project assets; neither is the user's current FaceEmo setup.
            foreach (var launcher in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (!IsLauncher(launcher) || !TargetsAvatar(authored, Member(launcher, "AV3Setting"))) continue;
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
                readRegistered(Member(menu, "Registered"));
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
            ref int serial, HashSet<object> visited, int depth, Func<string, bool> excludedPath = null, BindingSnapshot bindings = null)
        {
            var nextSerial = serial;
            try
            {
                VisitRegistered(list, prefix, visited, depth, (animation, path) =>
                    AddClip(avatar, animation, path, source, ref nextSerial, excludedPath, bindings));
            }
            finally { serial = nextSerial; }
        }

        private static void VisitRegistered(object list, string prefix, HashSet<object> visited, int depth, Action<object, string> addClip)
        {
            if (list == null) return;
            if (depth > 16 || !visited.Add(list)) throw new InvalidOperationException("FaceEmoのグループ参照が循環しているか深すぎます。");
            try
            {
                foreach (var item in OrderedItems(list))
                {
                    if (item.IsGroup)
                    {
                        VisitRegistered(item.Value, prefix + " / " + (Member(item.Value, "DisplayName") as string ?? "グループ"),
                            visited, depth + 1, addClip);
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
                    if (defaultAnimation != null) addClip(defaultAnimation, path + " / デフォルト");
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
                            addClip(animation, path + " / " + branchIndex + " / " + label);
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

        private static void AddClip(GameObject avatar, object animation, string path, VrChatExpressionMenu.Source source, ref int serial, Func<string, bool> excludedPath,
            BindingSnapshot bindings)
        {
            if (source.Entries.Count >= 512) throw new InvalidOperationException("表情候補が512件を超えています。");
            var entry = new VrChatExpressionMenu.Entry { Id = "faceemo/" + serial++, Name = path };
            try
            {
                var clip = Resolve(animation);
                if (clip == null) throw new InvalidOperationException("FaceEmoに登録されたアニメーションGUIDを解決できません。");
                entry.Name = path + " / " + clip.name;
                if (bindings == null) VrChatGestureExpressions.ReadClip(avatar, clip, entry, excludedPath);
                else bindings.ReadClip(avatar, clip, entry);
                VrChatExpressionSampler.ApplyPermanentOverrides(avatar, source.Controller, entry,
                    excludedPath: bindings == null ? excludedPath : bindings.PreparedExcludedPath);
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
