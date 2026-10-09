using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Evaluate the user's selected menu on a fresh graph. No SDK global
    // delegates, authored scene components or original assets are executed.
    internal static class ExperimentalInstalledExpressionPlayer
    {
        internal static List<VrChatExpressionMenu.MorphValue> Sample(GameObject avatar, VrChatExpressionMenu.Source source,
            IDictionary<string, float> selected, AnimatorState forcedState = null, int forcedLayer = -1)
        {
            var runtime = source.Controller;
            var controller = ExpressionDependencies.Controller(runtime);
            var context = FixedExpressionContext.Create(runtime, source.Defaults, source);
            var dependencies = new ExpressionDependencies();
            var unknown = new List<string>();
            var layers = ExpressionDependencies.Inspect(runtime, null, dependencies.Drivers, unknown, allowFxControls: true, typedConditions: true);
            if (unknown.Count > 0) throw new InvalidOperationException("表情再生に未対応の処理があります: " + string.Join(", ", unknown));
            dependencies.Layers.UnionWith(Enumerable.Range(0, layers.Length));
            dependencies.Parameters.UnionWith(selected.Keys);
            foreach (var layer in layers.Where(layer => layer.Morphs.Count > 0)) dependencies.Parameters.UnionWith(layer.Reads);
            bool changed;
            do
            {
                changed = false;
                foreach (var layer in layers)
                    if (layer.Writes.Overlaps(dependencies.Parameters))
                        foreach (var input in layer.Reads) changed |= dependencies.Parameters.Add(input);
            } while (changed);
            foreach (var control in layers.SelectMany(layer => layer.WeightControls))
            {
                if (control.Value.Playable != "FX") { dependencies.IgnoredWeightControls.Add(control.Key); continue; }
                if (!control.Value.AnimatorLayer || control.Value.BlendDuration != 0)
                    throw new InvalidOperationException("この表情の時間付きレイヤー制御は収録できません。");
                dependencies.EvaluatedWeightControls[control.Key] = control.Value;
            }
            var bindings = new HashSet<EditorCurveBinding>(layers.SelectMany(layer => layer.Morphs));
            ExpressionDependencies.ValidateAdditionalProbeBehaviours(runtime, source, context, bindings);
            using var evaluation = new ExpressionEvaluationSession(runtime, dependencies, source.ExpressionParameters, fixedContext: context, preserveSyncedOverrides: true);
            // Capture only the meshes selected for export in the prepared
            // hierarchy. A wardrobe initializer can activate unrelated meshes
            // during warmup; their values are not bindings of this capture.
            var includedPaths = new HashSet<string>(ExportRendererSelection.Enumerate(avatar).OfType<SkinnedMeshRenderer>()
                .Where(renderer => renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0)
                .Select(renderer => AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform)), StringComparer.Ordinal);
            var copy = Object.Instantiate(avatar);
            var materials = new List<Material>();
            var graph = default(PlayableGraph);
            try
            {
                copy.hideFlags = HideFlags.HideAndDontSave;
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(material => {
                        if (material == null) return null;
                        var owned = new Material(material) { hideFlags = HideFlags.HideAndDontSave }; materials.Add(owned); return owned;
                    }).ToArray();
                var animator = copy.GetComponent<Animator>() ?? copy.AddComponent<Animator>();
                animator.runtimeAnimatorController = null; animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.applyRootMotion = false; animator.fireEvents = false;
                evaluation.Animator = animator; copy.SetActive(true);
                graph = PlayableGraph.Create("VR Vlog installed expression capture"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, evaluation.Controller);
                AnimationPlayableOutput.Create(graph, "Completed face", animator).SetSourcePlayable(playable);
                void Set(IDictionary<string, float> values)
                {
                    foreach (var parameter in evaluation.Controller.parameters)
                    {
                        if (!values.TryGetValue(parameter.name, out var value)) continue;
                        if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("表情入力の値が不正です。");
                        switch (parameter.type)
                        {
                            case AnimatorControllerParameterType.Bool: playable.SetBool(parameter.name, value != 0); break;
                            case AnimatorControllerParameterType.Int: playable.SetInteger(parameter.name, Mathf.RoundToInt(value)); break;
                            case AnimatorControllerParameterType.Float: playable.SetFloat(parameter.name, value); break;
                            default: throw new InvalidOperationException("Trigger入力はこの表情収録では再現できません。");
                        }
                    }
                }
                List<VrChatExpressionMenu.MorphValue> Read() => ExportRendererSelection.Enumerate(copy).OfType<SkinnedMeshRenderer>()
                    .Where(renderer => renderer.sharedMesh != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                    .Where(renderer => includedPaths.Contains(AnimationUtility.CalculateTransformPath(renderer.transform, copy.transform)))
                    .SelectMany(renderer => Enumerable.Range(0, renderer.sharedMesh.blendShapeCount)
                        .Select(shape => new VrChatExpressionMenu.MorphValue { Path = AnimationUtility.CalculateTransformPath(renderer.transform, copy.transform),
                            Shape = renderer.sharedMesh.GetBlendShapeName(shape), Weight = renderer.GetBlendShapeWeight(shape) })).ToList();
                Set(source.Defaults); Set(context.Values); graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 90; frame++) { graph.Evaluate(1f / 60); evaluation.Check(); }
                // Compare appearance around the actual input change. Uncaptured
                // texture/bone/display effects must never become a partial face.
                var appearance = Appearance(copy);
                Set(selected); graph.Evaluate(0); evaluation.Check();
                if (forcedState != null)
                {
                    var originalLayers = controller.layers;
                    if (forcedLayer < 0 || forcedLayer >= originalLayers.Length) throw new InvalidOperationException("登録表情のレイヤー参照が不正です。");
                    var ownerLayer = forcedLayer; var seen = new HashSet<int>();
                    while (originalLayers[ownerLayer].syncedLayerIndex >= 0)
                    {
                        if (!seen.Add(ownerLayer)) throw new InvalidOperationException("登録表情の同期レイヤーが循環しています。");
                        ownerLayer = originalLayers[ownerLayer].syncedLayerIndex;
                        if (ownerLayer >= originalLayers.Length) throw new InvalidOperationException("登録表情の同期レイヤー参照が不正です。");
                    }
                    var paths = new List<string>();
                    void Find(AnimatorStateMachine machine, string prefix)
                    {
                        foreach (var child in machine.states) if (child.state == forcedState) paths.Add(prefix + "." + child.state.name);
                        foreach (var child in machine.stateMachines) Find(child.stateMachine, prefix + "." + child.stateMachine.name);
                    }
                    Find(originalLayers[ownerLayer].stateMachine, originalLayers[forcedLayer].name);
                    if (paths.Count != 1) throw new InvalidOperationException("登録表情の状態を一意に特定できません。");
                    playable.Play(Animator.StringToHash(paths[0]), forcedLayer, 0); graph.Evaluate(0); evaluation.Check();
                }
                List<VrChatExpressionMenu.MorphValue> previous = null, result = null;
                var settledFrames = 0;
                for (var frame = 0; frame < 120; frame++)
                {
                    graph.Evaluate(1f / 60); evaluation.Check(); result = Read();
                    if (previous != null && result.Count == previous.Count && result.Select((value, index) =>
                        Mathf.Abs(value.Weight - previous[index].Weight) < .001f).All(value => value)) settledFrames++;
                    else settledFrames = 0;
                    previous = result;
                }
                if (settledFrames < 30) throw new InvalidOperationException("動く表情のため、一括収録の固定時刻を確定できません。");
                ValidateStationary(playable, evaluation.Controller, dependencies.Parameters, includedPaths);
                var selectedAppearance = Appearance(copy);
                var difference = selectedAppearance.Keys.Concat(appearance.Keys).Distinct().FirstOrDefault(key =>
                    !appearance.TryGetValue(key, out var neutralValue) || !selectedAppearance.TryGetValue(key, out var selectedValue) || neutralValue != selectedValue);
                if (difference != null) throw new InvalidOperationException("材質・ボーン・表示切替を含む表情はこの固定形状収録へ部分的に取り込めません: " + difference);
                if (result.Any(value => float.IsNaN(value.Weight) || float.IsInfinity(value.Weight)))
                    throw new InvalidOperationException("再生後の表情に不正な変形量があります。");
                return result;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy(); Object.DestroyImmediate(copy);
                foreach (var material in materials) if (material != null) Object.DestroyImmediate(material);
            }
        }

        static void ValidateStationary(AnimatorControllerPlayable playable, AnimatorController controller, ISet<string> needed, ISet<string> includedPaths)
        {
            var parameters = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var parameter in controller.parameters)
                if (parameter.type != AnimatorControllerParameterType.Trigger)
                    parameters[parameter.name] = parameter.type == AnimatorControllerParameterType.Bool ? (playable.GetBool(parameter.name) ? 1 : 0) :
                        parameter.type == AnimatorControllerParameterType.Int ? playable.GetInteger(parameter.name) : playable.GetFloat(parameter.name);
            var layers = controller.layers;
            bool Relevant(EditorCurveBinding binding) => binding.type == typeof(Animator) ? needed.Contains(binding.propertyName) :
                binding.type != typeof(AudioSource) && (binding.type != typeof(SkinnedMeshRenderer) ||
                    !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) || includedPaths.Contains(binding.path));
            for (var layer = 0; layer < layers.Length; layer++)
            {
                if (layer != 0 && playable.GetLayerWeight(layer) == 0) continue;
                if (playable.IsInTransition(layer)) throw new InvalidOperationException("遷移中の表情は固定収録できません。");
                var owner = layer;
                while (layers[owner].syncedLayerIndex >= 0) owner = layers[owner].syncedLayerIndex;
                var stateInfo = playable.GetCurrentAnimatorStateInfo(layer);
                var hash = stateInfo.fullPathHash;
                // NDMF can retain an enabled empty FX layer. Unity reports no
                // current state and no active clip; there is no timed state to inspect.
                if (hash == 0 && playable.GetCurrentAnimatorClipInfo(layer).Length == 0) continue;
                var matched = false; AnimatorState currentState = null;
                void Visit(AnimatorStateMachine machine, string path)
                {
                    foreach (var child in machine.states)
                        if (Animator.StringToHash(path + "." + child.state.name) == hash)
                        {
                            matched = true; currentState = child.state;
                            if (child.state.transitions.Concat(machine.anyStateTransitions).Any(transition => !transition.mute && transition.hasExitTime &&
                                !ExpressionDependencies.IsFalse(transition, parameters)))
                                throw new InvalidOperationException("時間で遷移する表情は固定収録できません。");
                        }
                    foreach (var child in machine.stateMachines) Visit(child.stateMachine, path + "." + child.stateMachine.name);
                }
                foreach (var name in new[] { layers[layer].name, layers[owner].stateMachine.name }.Distinct()) Visit(layers[owner].stateMachine, name);
                if (!matched) throw new InvalidOperationException("再生中の表情状態を特定できません: " + layers[layer].name + " / " + hash);
                // Looking stable for two seconds cannot prove a delayed or
                // looping clip is stationary. Inspect its complete curves.
                foreach (var clip in playable.GetCurrentAnimatorClipInfo(layer).Where(info => info.weight > .00001f).Select(info => info.clip))
                {
                    if (AnimationUtility.GetAnimationEvents(clip).Length != 0)
                        throw new InvalidOperationException("Animation Eventを含む表情は固定収録できません。");
                    // A non-looping, unretimed single clip has a native held
                    // endpoint after normalized time one. It is already complete.
                    if (!stateInfo.loop && stateInfo.normalizedTime >= 1 && currentState.speed > 0 &&
                        !currentState.speedParameterActive && !currentState.timeParameterActive &&
                        controller.GetStateEffectiveMotion(currentState, layer) == clip && !clip.isLooping) continue;
                    var varying = AnimationUtility.GetCurveBindings(clip).Where(Relevant).Where(binding => !VrChatExpressionSampler.IsConstant(AnimationUtility.GetEditorCurve(clip, binding)))
                        .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(Relevant).Where(binding => AnimationUtility.GetObjectReferenceCurve(clip, binding).Select(key => key.value).Distinct().Count() > 1)).ToArray();
                    if (varying.Length > 0) throw new InvalidOperationException("時間で変わる曲線を含む表情は固定収録できません: " + layers[layer].name + " / " + clip.name + " / " + varying[0].path + " / " + varying[0].propertyName);
                }
            }
        }

        static Dictionary<string, string> Appearance(GameObject root)
        {
            string Number(float value) => (Mathf.Abs(value) < .000005f ? 0 : value).ToString("F5", CultureInfo.InvariantCulture);
            string Vector(Vector3 value) => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var path = AnimationUtility.CalculateTransformPath(transform, root.transform);
                result[path + " / position"] = Vector(transform.localPosition);
                result[path + " / scale"] = Vector(transform.localScale);
                result[path + " / rotation"] = Vector(transform.localEulerAngles);
                result[path + " / active"] = transform.gameObject.activeSelf.ToString();
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform) + " / " + renderer.GetType().Name;
                result[path + " / enabled"] = renderer.enabled.ToString();
                var slot = 0;
                foreach (var material in renderer.sharedMaterials)
                {
                    var prefix = path + " / material " + slot++;
                    result[prefix] = material == null ? "null" : material.shader.name + " / " + material.renderQueue + " / " + string.Join(",", material.shaderKeywords.OrderBy(keyword => keyword, StringComparer.Ordinal));
                    if (material == null) continue;
                    for (var index = 0; index < ShaderUtil.GetPropertyCount(material.shader); index++)
                    {
                        var name = ShaderUtil.GetPropertyName(material.shader, index);
                        switch (ShaderUtil.GetPropertyType(material.shader, index))
                        {
                            case ShaderUtil.ShaderPropertyType.Color: result[prefix + " / " + name] = material.GetColor(name).ToString("F5"); break;
                            case ShaderUtil.ShaderPropertyType.Vector: result[prefix + " / " + name] = material.GetVector(name).ToString("F5"); break;
                            case ShaderUtil.ShaderPropertyType.Float:
                            case ShaderUtil.ShaderPropertyType.Range: result[prefix + " / " + name] = Number(material.GetFloat(name)); break;
                            case ShaderUtil.ShaderPropertyType.TexEnv:
                                var texture = material.GetTexture(name);
                                result[prefix + " / " + name] = (texture == null ? 0 : texture.GetInstanceID()) + " / " +
                                    material.GetTextureOffset(name).ToString("F5") + " / " + material.GetTextureScale(name).ToString("F5"); break;
                        }
                    }
                }
            }
            return result;
        }
    }
}
