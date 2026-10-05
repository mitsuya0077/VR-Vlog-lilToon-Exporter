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
            PreparedExpressionBindings bindings, Func<string, bool> excludedPath = null, BindingSnapshot registeredBindings = null)
        {
            if (bindings == null) throw new ArgumentNullException(nameof(bindings));
            var deferred = registeredBindings?.DeferPermanentOverrides == true;
            ApplyPreparedDefaultFace(prepared, source, bindings.AuthoringPaths, path => bindings.Get(path)?.Renderer, excludedPath,
                composeValues: !deferred);
            registeredBindings?.ApplyDeferredOverrides(prepared, source);
        }

        private static void ApplyPreparedDefaultFace(GameObject prepared, VrChatExpressionMenu.Source source,
            Func<string, string[]> toAuthoringPaths, Func<string, SkinnedMeshRenderer> toPreparedRenderer,
            Func<string, bool> excludedPath, bool composeValues = true)
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
                // The export's native PLAYER-slot probe evaluates this common
                // layer with its actual weight and order. Copying its raw value
                // into the selected clip would bypass fractional layer blending.
                if (composeValues) ComposeCommonValues(prepared, entries, underlay, toAuthoringPaths, toPreparedRenderer);
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

        internal sealed class PlayerSelection
        {
            internal RuntimeAnimatorController Runtime;
            internal AnimatorStateMachine Machine;
            internal AnimatorState State;
            internal int? Layer;
            internal bool WriteDefaults;
            internal string StateProof;
            internal AnimationClip Motion;
            internal ClipProof MotionProof;
            internal bool ReadPreparedMotion;
        }

        // A registered GUID proves a clip, not arbitrary callbacks on its
        // entire PLAYER layer. Retargeted clips may have another identity;
        // their complete curve/object/timing data must then agree exactly.
        internal sealed class ClipProof
        {
            private readonly Dictionary<EditorCurveBinding, AnimationCurve> curves;
            private readonly Dictionary<EditorCurveBinding, ObjectReferenceKeyframe[]> objects;
            private readonly float frameRate, length;
            private readonly bool looping;
            private readonly WrapMode wrapMode;
            internal ClipProof(AnimationClip clip)
            {
                curves = AnimationUtility.GetCurveBindings(clip).ToDictionary(binding => binding,
                    binding => AnimationUtility.GetEditorCurve(clip, binding));
                objects = AnimationUtility.GetObjectReferenceCurveBindings(clip).ToDictionary(binding => binding,
                    binding => AnimationUtility.GetObjectReferenceCurve(clip, binding));
                frameRate = clip.frameRate; length = clip.length; looping = clip.isLooping; wrapMode = clip.wrapMode;
            }
            internal bool Matches(AnimationClip clip)
            {
                if (clip == null || clip.frameRate != frameRate || clip.length != length || clip.isLooping != looping || clip.wrapMode != wrapMode ||
                    AnimationUtility.GetAnimationEvents(clip).Length != 0) return false;
                var bindings = AnimationUtility.GetCurveBindings(clip);
                if (!new HashSet<EditorCurveBinding>(bindings).SetEquals(curves.Keys)) return false;
                foreach (var binding in bindings)
                {
                    var expected = curves[binding]; var actual = AnimationUtility.GetEditorCurve(clip, binding);
                    if (expected == null || actual == null || expected.preWrapMode != actual.preWrapMode || expected.postWrapMode != actual.postWrapMode ||
                        !expected.keys.SequenceEqual(actual.keys)) return false;
                }
                var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                if (!new HashSet<EditorCurveBinding>(objectBindings).SetEquals(objects.Keys)) return false;
                foreach (var binding in objectBindings)
                {
                    var expected = objects[binding]; var actual = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (expected.Length != actual.Length || expected.Where((value, index) =>
                        value.time != actual[index].time || value.value != actual[index].value).Any()) return false;
                }
                return true;
            }
        }

        private static string StateProof(AnimatorState state) => EditorJsonUtility.ToJson(state) + "\n" +
            string.Join("\n", state.behaviours.Select(behaviour => behaviour == null ? "<missing>" :
                behaviour.GetType().FullName + " / " + EditorJsonUtility.ToJson(behaviour)));

        private static AnimationClip EffectiveClip(RuntimeAnimatorController runtime, AnimatorState state)
        {
            if (!(state?.motion is AnimationClip original)) return null;
            return ExpressionDependencies.Overrides(runtime).TryGetValue(original, out var replacement) ? replacement : original;
        }

        private static PlayerSelection ResolvePlayer(RuntimeAnimatorController runtime, AnimationClip registered, AnimationClip prepared,
            Func<AnimationClip, bool> preparedOrigin = null)
        {
            var result = new PlayerSelection { Runtime = runtime };
            if (runtime == null) return result;
            var controller = ExpressionDependencies.Controller(runtime);
            var layers = controller.layers;
            var players = Enumerable.Range(0, layers.Length).Where(index => layers[index].name == "[ USER EDIT ] FACE EMOTE PLAYER").ToArray();
            if (players.Length > 1 || players.Length == 0 && layers.Any(layer => layer.name == "[ USER EDIT ] DEFAULT FACE"))
                throw new InvalidOperationException("FaceEmoの表情FXレイヤーを一意に特定できません。");
            if (players.Length == 0) return result; // Authored registrations without generated PLAYER remain standalone clips.
            var layer = layers[players[0]];
            if (layer.syncedLayerIndex >= 0 || layer.stateMachine == null || layer.blendingMode != AnimatorLayerBlendingMode.Override)
                throw new InvalidOperationException("FaceEmoの表情FXレイヤーの合成方法を確定できません。");
            var replacements = ExpressionDependencies.Overrides(runtime);
            var proof = new ClipProof(prepared);
            var visited = new HashSet<AnimatorStateMachine>();
            var states = new HashSet<AnimatorState>();
            var exact = new List<AnimatorState>(); var equivalent = new List<AnimatorState>();
            void Visit(AnimatorStateMachine machine)
            {
                if (machine == null || !visited.Add(machine)) throw new InvalidOperationException("FaceEmoの表情FX状態の階層が不正です。");
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null || !states.Add(state)) throw new InvalidOperationException("FaceEmoの表情FX状態を一意に取得できません。");
                    if (!(state.motion is AnimationClip original)) continue;
                    var clip = replacements.TryGetValue(original, out var replacement) ? replacement : original;
                    // Original asset identity cannot prove the effective
                    // motion after AnimatorOverrideController replacement.
                    // Only the effective registered clip is an exact match;
                    // an overridden retargeted copy must prove all its data.
                    if (clip == registered || preparedOrigin?.Invoke(clip) == true) exact.Add(state);
                    else if (proof.Matches(clip)) equivalent.Add(state);
                }
                foreach (var child in machine.stateMachines) Visit(child.stateMachine);
            }
            Visit(layer.stateMachine);
            var matched = exact.Count > 0 ? exact : equivalent;
            if (matched.Count != 1)
            {
                var reason = matched.Select(state => state.writeDefaultValues).Distinct().Count() > 1 ? "Write Defaultsが一致しません" :
                    matched.Count == 0 ? "登録クリップに対応する状態がありません" : "登録クリップに対応する状態が重複しています";
                throw new InvalidOperationException("FaceEmoの登録クリップに対応するFX状態を一意に特定できません: " + registered.name + " / " + reason);
            }
            result.Layer = players[0]; result.Machine = layer.stateMachine; result.State = matched.Single();
            result.WriteDefaults = result.State.writeDefaultValues; result.StateProof = StateProof(result.State);
            result.Motion = EffectiveClip(runtime, result.State); result.MotionProof = new ClipProof(result.Motion);
            result.ReadPreparedMotion = result.Motion != registered && preparedOrigin?.Invoke(result.Motion) == true;
            return result;
        }

        // FaceEmo's GUID clips are outside the FX controller rewritten by MA.
        // Remember their targets on our copy before Transforming moves them;
        // evaluate the clips only after the merged FX controller is available.
        internal sealed class BindingSnapshot
        {
            private readonly GameObject clone;
            private readonly Func<string, bool> originalExcludedPath;
            internal Func<string, bool> PreparedExcludedPath { get; }
            internal bool DeferPermanentOverrides { get; }
            private readonly Dictionary<VrChatExpressionMenu.Entry, PlayerSelection> deferredEntries =
                new Dictionary<VrChatExpressionMenu.Entry, PlayerSelection>();
            private object objectRegistry;
            private Func<UnityEngine.Object, UnityEngine.Object> isolatedCopyOf;
            private sealed class Target
            {
                internal Component[] Matches;
                internal bool Excluded;
                internal bool LostRendererProvenance;
            }
            private readonly Dictionary<AnimationClip, Dictionary<EditorCurveBinding, Target>> targets =
                new Dictionary<AnimationClip, Dictionary<EditorCurveBinding, Target>>();

            internal BindingSnapshot(GameObject clone, Func<string, bool> excludedPath = null, Func<string, bool> preparedExcludedPath = null,
                bool deferPermanentOverrides = false)
            {
                this.clone = clone; originalExcludedPath = excludedPath; PreparedExcludedPath = preparedExcludedPath;
                DeferPermanentOverrides = deferPermanentOverrides;
            }

            internal void RebindPrepared(Func<SkinnedMeshRenderer, SkinnedMeshRenderer> replacement,
                object registry = null, Func<UnityEngine.Object, UnityEngine.Object> isolatedCopyOf = null)
            {
                if (replacement == null) throw new ArgumentNullException(nameof(replacement));
                objectRegistry = registry; this.isolatedCopyOf = isolatedCopyOf;
                var mapped = new Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer>();
                foreach (var target in targets.Values.SelectMany(paths => paths.Values))
                    for (var index = 0; index < target.Matches.Length; index++)
                        if (target.Matches[index] is SkinnedMeshRenderer renderer && !target.Excluded)
                        {
                            if (!mapped.TryGetValue(renderer, out var current)) mapped.Add(renderer, current = replacement(renderer));
                            if (current != null) target.Matches[index] = current;
                            else target.LostRendererProvenance = true;
                        }
            }

            private bool PreparedClipHasOrigin(AnimationClip clip, AnimationClip registered)
            {
                if (clip == null || objectRegistry == null) return false;
                // NDMF records committed animator clips against the original
                // asset. Read that existing provenance without creating a
                // reference; augmented clips need not have identical curves.
                var contract = objectRegistry.GetType().GetInterfaces().FirstOrDefault(type =>
                    type.FullName == "nadena.dev.ndmf.IObjectRegistry");
                var method = contract?.GetMethod("GetReference", new[] { typeof(UnityEngine.Object), typeof(bool) });
                if (method == null) return false;
                try
                {
                    var reference = method.Invoke(objectRegistry, new object[] { clip, false });
                    var origin = reference?.GetType().GetProperty("Object", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(reference) as UnityEngine.Object;
                    return origin != null && (origin == registered || origin == isolatedCopyOf?.Invoke(registered));
                }
                catch (TargetInvocationException) { return false; }
                catch (ArgumentException) { return false; }
            }

            internal void Defer(VrChatExpressionMenu.Entry entry, PlayerSelection selected)
                => deferredEntries.Add(entry, selected);

            internal void ApplyDeferredOverrides(GameObject prepared, VrChatExpressionMenu.Source source)
            {
                if (!DeferPermanentOverrides) return;
                if (prepared != clone) throw new InvalidOperationException("FaceEmoの表情参照は書き出し用コピーと一致していません。");
                foreach (var pair in deferredEntries)
                {
                    var entry = pair.Key; var captured = pair.Value;
                    if (entry.Error != null || !source.Entries.Contains(entry)) continue;
                    try
                    {
                        if (captured.Runtime != source.Controller || captured.Layer.HasValue &&
                            (captured.Layer.Value >= ExpressionDependencies.Controller(source.Controller).layers.Length ||
                             ExpressionDependencies.Controller(source.Controller).layers[captured.Layer.Value].stateMachine != captured.Machine) ||
                            captured.State != null && (StateProof(captured.State) != captured.StateProof ||
                                EffectiveClip(captured.Runtime, captured.State) != captured.Motion ||
                                !captured.MotionProof.Matches(captured.Motion)))
                            throw new InvalidOperationException("FaceEmoの表情FXレイヤーが評価前に変わりました。");
                        VrChatExpressionSampler.ApplyPermanentOverrides(prepared, captured.Runtime, entry,
                            captured.Layer, captured.WriteDefaults, PreparedExcludedPath, metadata: source, sourceState: captured.State);
                    }
                    catch (InvalidOperationException error) { entry.Error = error.Message; }
                }
                deferredEntries.Clear();
            }

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

            internal PlayerSelection ReadClip(GameObject avatar, AnimationClip original, VrChatExpressionMenu.Entry entry,
                RuntimeAnimatorController runtime = null)
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
                    if (captured.LostRendererProvenance)
                        throw new InvalidOperationException(NdmfExportPreparation.UnknownRendererRelocation + " (FaceEmo: " + binding.path + ")");
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
                    var selected = ResolvePlayer(runtime, original, clip, candidate => PreparedClipHasOrigin(candidate, original));
                    // A registry-proven NDMF replacement is authoritative:
                    // its additional synchronized channels are part of the
                    // prepared expression, not optional matching evidence.
                    var read = selected.ReadPreparedMotion ? selected.Motion : clip;
                    VrChatGestureExpressions.ReadClip(avatar, read, entry, PreparedExcludedPath);
                    if (entry.Animation.Count > 0)
                    {
                        if (read.length <= 0 || read.length > 600)
                            throw new InvalidOperationException("表情アニメーションの長さは0秒より長く600秒以下である必要があります: " + original.name);
                        entry.Duration = read.length;
                        entry.Loop = read.isLooping;
                    }
                    return selected;
                }
                finally { UnityEngine.Object.DestroyImmediate(clip); }
            }
        }

        internal static BindingSnapshot Capture(GameObject authoringSource, GameObject clone, Func<string, bool> excludedPath = null,
            Func<string, bool> preparedExcludedPath = null, bool deferPermanentOverrides = false)
        {
            var snapshot = new BindingSnapshot(clone, excludedPath, preparedExcludedPath, deferPermanentOverrides);
            ReadRepositories(authoringSource, new VrChatExpressionMenu.Source(), registered =>
                CaptureRegistered(clone, registered, snapshot));
            return snapshot;
        }

        // Shares the serialized-data route with real FaceEmo discovery so tests
        // can cover path remapping without installing its optional SDK.
        internal static BindingSnapshot CaptureRegistered(GameObject clone, object registered, BindingSnapshot snapshot = null, Func<string, bool> excludedPath = null,
            Func<string, bool> preparedExcludedPath = null, bool deferPermanentOverrides = false)
        {
            snapshot = snapshot ?? new BindingSnapshot(clone, excludedPath, preparedExcludedPath, deferPermanentOverrides);
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
                PlayerSelection selected;
                if (bindings == null)
                {
                    VrChatGestureExpressions.ReadClip(avatar, clip, entry, excludedPath);
                    selected = ResolvePlayer(source.Controller, clip, clip);
                }
                else selected = bindings.ReadClip(avatar, clip, entry, source.Controller);
                if (bindings?.DeferPermanentOverrides == true) bindings.Defer(entry, selected);
                else VrChatExpressionSampler.ApplyPermanentOverrides(avatar, source.Controller, entry, selected.Layer, selected.WriteDefaults,
                    excludedPath: bindings == null ? excludedPath : bindings.PreparedExcludedPath, metadata: source, sourceState: selected.State);
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
