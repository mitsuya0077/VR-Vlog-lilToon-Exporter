using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Optional SDK metadata can establish that a declared signal is not a
    // persistent or menu-controlled input. Unknown serialized data never
    // establishes that absence. Read assets only; do not invoke SDK getters.
    internal static class NeutralInputProof
    {
        const string DescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        const string ParametersType = "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters";
        const string MenuType = "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu";

        internal static void Read(GameObject avatar, VrChatExpressionMenu.Source source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            source.NeutralInputInventoryComplete = false;
            source.MenuInputs.Clear();
            source.ParameterPersistence.Clear();
            source.ExpressionParameterTypes.Clear();
            if (avatar == null) return;
            var descriptors = avatar.GetComponents<Component>().Where(component => component != null &&
                component.GetType().FullName == DescriptorType).ToArray();
            if (descriptors.Length != 1) return;
            try
            {
                using var descriptor = new SerializedObject(descriptors[0]);
                descriptor.Update();
                var enabled = descriptor.FindProperty("customExpressions");
                if (!Is(enabled, SerializedPropertyType.Boolean)) return;
                var parameterAsset = descriptor.FindProperty("expressionParameters");
                var parametersKnown = Is(parameterAsset, SerializedPropertyType.ObjectReference) &&
                    (parameterAsset.objectReferenceValue == null || ReadParameters(parameterAsset.objectReferenceValue, source));
                if (!enabled.boolValue)
                {
                    source.NeutralInputInventoryComplete = true;
                    return;
                }
                if (!parametersKnown) return;
                var menu = descriptor.FindProperty("expressionsMenu");
                if (!Is(menu, SerializedPropertyType.ObjectReference) || menu.objectReferenceValue == null) return;
                source.NeutralInputInventoryComplete = ReadMenus(menu.objectReferenceValue, source.MenuInputs);
            }
            catch (Exception)
            {
                // A missing/incompatible SDK schema or unreadable Unity asset
                // cannot prove a transient internal input. Keep it unknown.
                source.NeutralInputInventoryComplete = false;
                source.MenuInputs.Clear();
                source.ParameterPersistence.Clear();
                source.ExpressionParameterTypes.Clear();
            }
        }

        static bool ReadParameters(UnityEngine.Object asset, VrChatExpressionMenu.Source source)
        {
            if (asset.GetType().FullName != ParametersType) return false;
            using var data = new SerializedObject(asset);
            data.Update();
            var parameters = data.FindProperty("parameters");
            if (parameters == null || !parameters.isArray || parameters.arraySize > VrChatExpressionMenu.MaximumControlVisits) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var invalid = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < parameters.arraySize; index++)
            {
                var parameter = parameters.GetArrayElementAtIndex(index);
                var nameProperty = parameter.FindPropertyRelative("name");
                if (!Is(nameProperty, SerializedPropertyType.String))
                {
                    source.ParameterPersistence.Clear();
                    source.ExpressionParameterTypes.Clear();
                    return false;
                }
                if (string.IsNullOrEmpty(nameProperty.stringValue)) continue;
                var name = nameProperty.stringValue;
                var saved = parameter.FindPropertyRelative("saved");
                var network = parameter.FindPropertyRelative("networkSynced");
                var type = EnumName(parameter.FindPropertyRelative("valueType"));
                if (!seen.Add(name) || !Is(saved, SerializedPropertyType.Boolean) || !Is(network, SerializedPropertyType.Boolean) ||
                    (type != "Bool" && type != "Int" && type != "Float")) invalid.Add(name);
                if (invalid.Contains(name))
                {
                    source.ParameterPersistence.Remove(name);
                    source.ExpressionParameterTypes.Remove(name);
                    continue;
                }
                source.ParameterPersistence[name] = (saved.boolValue, network.boolValue);
                source.ExpressionParameterTypes[name] = type;
            }
            return true;
        }

        static bool ReadMenus(UnityEngine.Object root, HashSet<string> inputs)
        {
            var pending = new Stack<UnityEngine.Object>();
            var visited = new HashSet<int>();
            var controlVisits = 0;
            var inputVisits = 0;
            pending.Push(root);
            while (pending.Count != 0)
            {
                var asset = pending.Pop();
                if (asset == null || asset.GetType().FullName != MenuType) return false;
                if (!visited.Add(asset.GetInstanceID())) continue;
                if (visited.Count > VrChatExpressionMenu.MaximumMenuVisits) return false;
                using var data = new SerializedObject(asset);
                data.Update();
                var controls = data.FindProperty("controls");
                if (controls == null || !controls.isArray || controls.arraySize > VrChatExpressionMenu.MaximumControlVisits - controlVisits) return false;
                for (var index = 0; index < controls.arraySize; index++)
                {
                    controlVisits++;
                    var control = controls.GetArrayElementAtIndex(index);
                    var kind = EnumName(control.FindPropertyRelative("type"));
                    if (kind != "Button" && kind != "Toggle" && kind != "SubMenu" && kind != "TwoAxisPuppet" &&
                        kind != "FourAxisPuppet" && kind != "RadialPuppet") return false;
                    if (!ReadInput(control.FindPropertyRelative("parameter"), inputs, ref inputVisits)) return false;
                    var subParameters = control.FindPropertyRelative("subParameters");
                    if (subParameters == null || !subParameters.isArray || subParameters.arraySize >
                        VrChatExpressionMenu.MaximumControlVisits * 5 - inputVisits) return false;
                    for (var subIndex = 0; subIndex < subParameters.arraySize; subIndex++)
                        if (!ReadInput(subParameters.GetArrayElementAtIndex(subIndex), inputs, ref inputVisits)) return false;
                    if (kind != "SubMenu") continue;
                    var subMenu = control.FindPropertyRelative("subMenu");
                    if (!Is(subMenu, SerializedPropertyType.ObjectReference) || subMenu.objectReferenceValue == null) return false;
                    pending.Push(subMenu.objectReferenceValue);
                }
            }
            return true;
        }

        static bool ReadInput(SerializedProperty parameter, HashSet<string> inputs, ref int visits)
        {
            if (++visits > VrChatExpressionMenu.MaximumControlVisits * 5 || parameter == null) return false;
            var name = parameter.FindPropertyRelative("name");
            if (!Is(name, SerializedPropertyType.String)) return false;
            if (!string.IsNullOrEmpty(name.stringValue)) inputs.Add(name.stringValue);
            return true;
        }

        static bool Is(SerializedProperty property, SerializedPropertyType type) => property != null && property.propertyType == type;

        static string EnumName(SerializedProperty property)
        {
            if (!Is(property, SerializedPropertyType.Enum)) return null;
            var index = property.enumValueIndex;
            var names = property.enumNames;
            return index >= 0 && index < names.Length ? names[index] : null;
        }
    }
}
