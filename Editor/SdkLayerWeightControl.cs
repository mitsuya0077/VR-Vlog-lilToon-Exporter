using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Decode documented SDK metadata without invoking runtime callbacks. A
    // recognized but malformed command is distinct from an unknown behaviour
    // and from a valid effect that the exporter cannot reproduce.
    internal sealed class SdkLayerWeightControl
    {
        internal string Location;
        internal bool AnimatorLayer;
        internal string Playable;
        internal int LayerIndex;
        internal float GoalWeight, BlendDuration;
        // Filled by graph inspection, not inferred from the SDK object's name.
        internal AnimatorState SourceState;
        internal bool FixedBaseLayer;

        internal static SdkLayerWeightControl Read(StateMachineBehaviour value, string location, int fxLayerCount)
        {
            if (value == null) return null;
            var type = value.GetType();
            var animatorLayer = type.FullName == "VRC.SDK3.Avatars.Components.VRCAnimatorLayerControl" ||
                type.FullName == "VRC.SDKBase.VRC_AnimatorLayerControl";
            var playableLayer = type.FullName == "VRC.SDK3.Avatars.Components.VRCPlayableLayerControl" ||
                type.FullName == "VRC.SDKBase.VRC_PlayableLayerControl";
            if (!animatorLayer && !playableLayer) return null;

            InvalidOperationException Invalid(string reason) => new InvalidOperationException(
                ExporterLocalization.T("FXのレイヤー制御に不正な設定があります: ") + location + " / " + type.Name + " / " + reason);
            using (var data = new SerializedObject(value))
            {
                SerializedProperty Require(string name, SerializedPropertyType expected)
                {
                    var property = data.FindProperty(name);
                    if (property == null || property.propertyType != expected)
                        throw Invalid(name + " の設定を読み取れません。");
                    return property;
                }

                var targetName = animatorLayer ? "playable" : "layer";
                var target = Require(targetName, SerializedPropertyType.Enum);
                var enumIndex = target.enumValueIndex;
                if (enumIndex < 0 || enumIndex >= target.enumNames.Length)
                    throw Invalid(targetName + " の列挙値が不正です。");
                var playable = target.enumNames[enumIndex];
                if (playable != "Action" && playable != "Gesture" && playable != "Additive" && playable != "FX")
                    throw Invalid(targetName + " の対象が不正です: " + playable);

                var goal = Require("goalWeight", SerializedPropertyType.Float).floatValue;
                if (!Finite(goal) || goal < 0 || goal > 1)
                    throw Invalid("goalWeight は 0 から 1 の有限値である必要があります。");
                var duration = Require("blendDuration", SerializedPropertyType.Float).floatValue;
                if (!Finite(duration) || duration < 0)
                    throw Invalid("blendDuration は 0 以上の有限値である必要があります。");

                var layerIndex = -1;
                if (animatorLayer)
                {
                    layerIndex = Require("layer", SerializedPropertyType.Integer).intValue;
                    // This count describes the prepared FX controller only.
                    // Other playable controllers may have different layer counts.
                    // Their runtime range is not proved here; neutral preparation
                    // retains body pose and never executes these body controls.
                    if (layerIndex < 0 || playable == "FX" && layerIndex >= fxLayerCount)
                        throw Invalid("layer のインデックスが対象範囲外です: " + layerIndex);
                }
                return new SdkLayerWeightControl
                {
                    Location = location, AnimatorLayer = animatorLayer, Playable = playable,
                    LayerIndex = layerIndex, GoalWeight = goal, BlendDuration = duration, FixedBaseLayer = animatorLayer && layerIndex == 0
                };
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
