using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRVlog.LilToonExporter
{
    // A candidate no-op is not permission to discard a real appearance change.
    // Native callers must also retain their layer/WD/additive context checks.
    // Read only prepared values; never instantiate or edit source materials.
    internal static class SelectedExpressionAppearance
    {
        internal static void ValidateClipData(AnimationClip clip, Func<string, bool> excludedPath = null)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (AnimationUtility.GetAnimationEvents(clip).Length != 0)
                throw new InvalidOperationException("表情アニメーションにAnimationEventが含まれます: " + clip.name);
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (excludedPath?.Invoke(binding.path) != true)
                    VrChatExpressionSampler.ValidateNativeParameterCurve(AnimationUtility.GetEditorCurve(clip, binding),
                        clip.name + " / " + binding.path + " / " + binding.propertyName);
        }

        internal static bool IsUnchanged(GameObject avatar, AnimationClip clip, EditorCurveBinding binding)
        {
            if (avatar == null || clip == null || binding.path == null || binding.propertyName == null || binding.isPPtrCurve) return false;
            var activation = binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive";
            var rendererBinding = binding.type == typeof(Renderer) || binding.type == typeof(MeshRenderer) || binding.type == typeof(SkinnedMeshRenderer);
            var enabled = rendererBinding && binding.propertyName == "m_Enabled";
            var material = rendererBinding && binding.propertyName.StartsWith("material.", StringComparison.Ordinal);
            if (!activation && !enabled && !material) return false;
            if (!ConstantValue(clip, binding, out var value)) return false;
            if ((activation || enabled) && value != 0 && value != 1) return false;

            var matches = avatar.GetComponentsInChildren<Transform>(true).Where(transform =>
                AnimationUtility.CalculateTransformPath(transform, avatar.transform) == binding.path).ToArray();
            if (matches.Length == 0) return true; // Unity cannot bind an absent prepared target.
            if (matches.Length != 1)
                throw new InvalidOperationException("表情の見た目の対象を一意に指定できません: " + clip.name + " / " + binding.path);
            var target = matches[0].gameObject;
            if (activation) return target.activeSelf == (value != 0);
            var components = target.GetComponents(binding.type);
            if (components.Length == 0) return true;
            if (components.Length != 1)
                throw new InvalidOperationException("表情の見た目のComponentを一意に指定できません: " + clip.name + " / " + binding.path);
            var renderer = (Renderer)components[0];
            if (enabled) return renderer.enabled == (value != 0);
            // Per-renderer/per-slot overrides can replace material values. Do
            // not infer their effective precedence or remove such overrides.
            if (renderer.HasPropertyBlock()) return false;
            var materials = renderer.sharedMaterials;
            if (materials.Length == 0) return false;
            var property = binding.propertyName.Substring("material.".Length);
            foreach (var item in materials)
                if (!MatchesMaterialValue(item, property, value)) return false;
            return true;
        }

        private static bool ConstantValue(AnimationClip clip, EditorCurveBinding binding, out float value)
        {
            value = 0;
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            VrChatExpressionSampler.ValidateNativeParameterCurve(curve, clip.name + " / " + binding.path + " / " + binding.propertyName);
            if (curve == null || curve.length == 0) return false;
            var keys = curve.keys; value = keys[0].value;
            if (keys.Any(key => key.value != keys[0].value)) return false;
            // Unused first-in/last-out tangents cannot change a segment. Active
            // nonzero tangents could overshoot despite equal endpoint values.
            for (var i = 0; i + 1 < keys.Length; i++)
                if (keys[i].outTangent != 0 && !float.IsInfinity(keys[i].outTangent) ||
                    keys[i + 1].inTangent != 0 && !float.IsInfinity(keys[i + 1].inTangent)) return false;
            return true;
        }

        private static bool MatchesMaterialValue(Material material, string property, float value)
        {
            if (material == null || material.shader == null || string.IsNullOrEmpty(property)) return false;
            var shader = material.shader;
            var index = shader.FindPropertyIndex(property);
            if (index >= 0)
            {
                var type = shader.GetPropertyType(index);
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range) return false;
                var current = material.GetFloat(property);
                return Finite(current) && current == value;
            }
            if (property.Length < 3 || property[property.Length - 2] != '.') return false;
            var channel = "rgba".IndexOf(property[property.Length - 1]);
            if (channel < 0) return false;
            var name = property.Substring(0, property.Length - 2);
            index = shader.FindPropertyIndex(name);
            if (index < 0 || shader.GetPropertyType(index) != ShaderPropertyType.Color) return false;
            var color = material.GetColor(name)[channel];
            if (!Finite(color)) return false;
            if (color == value) return true;
            // In Linear projects Unity's ordinary Color APIs can round-trip
            // gamma/linear representations, returning .24999997 for an authored
            // .25. Match that conversion exactly, never use a generic epsilon. HDR,
            // alpha and scalar properties retain their exact-value comparison.
            return channel < 3 && QualitySettings.activeColorSpace == ColorSpace.Linear &&
                (shader.GetPropertyFlags(index) & ShaderPropertyFlags.HDR) == 0 &&
                Mathf.LinearToGammaSpace(Mathf.GammaToLinearSpace(value)) == color;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
