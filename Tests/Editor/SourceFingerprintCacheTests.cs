using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class SourceFingerprintCacheTests
    {
        [TestCase("float")]
        [TestCase("color")]
        [TestCase("texture")]
        public void MaterialDefaultCachePopulationDoesNotInvalidateButAnEffectiveEditDoes(string kind)
        {
            var source = new GameObject("Source");
            var material = new Material(Shader.Find("Standard"));
            var defaults = new Material(material.shader);
            var texture = new Texture2D(2, 2);
            try
            {
                source.AddComponent<MeshRenderer>().sharedMaterial = material;
                var property = kind == "float" ? "_Glossiness" : kind == "color" ? "_Color" : "_MainTex";
                using (var data = new SerializedObject(material))
                {
                    var array = data.FindProperty("m_SavedProperties." + (kind == "float" ? "m_Floats" : kind == "color" ? "m_Colors" : "m_TexEnvs"));
                    for (var i = array.arraySize - 1; i >= 0; i--)
                        if (array.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue == property)
                            array.DeleteArrayElementAtIndex(i);
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var scalar = defaults.GetFloat("_Glossiness"); var color = defaults.GetColor("_Color");
                var originalTexture = defaults.GetTexture("_MainTex");
                var scale = defaults.GetTextureScale("_MainTex"); var offset = defaults.GetTextureOffset("_MainTex");
                var before = ExportSourceFingerprint.Compute(source);
                if (kind == "float") material.SetFloat(property, scalar);
                else if (kind == "color") material.SetColor(property, color);
                else { material.SetTexture(property, originalTexture); material.SetTextureScale(property, scale); material.SetTextureOffset(property, offset); }
                Assert.That(ExportSourceFingerprint.Compute(source), Is.EqualTo(before), "Only the explicit storage of an already-effective shader default changed.");
                if (kind == "float") material.SetFloat(property, scalar + .125f);
                else if (kind == "color") material.SetColor(property, Color.magenta);
                else material.SetTexture(property, texture);
                Assert.That(ExportSourceFingerprint.Compute(source), Is.Not.EqualTo(before));
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(material); Object.DestroyImmediate(defaults); Object.DestroyImmediate(texture); }
        }

        [Test]
        public void UnknownSavedMaterialPropertiesAndTextureTransformsStillInvalidate()
        {
            var source = new GameObject("Source"); var material = new Material(Shader.Find("Standard"));
            try
            {
                source.AddComponent<MeshRenderer>().sharedMaterial = material;
                using (var data = new SerializedObject(material))
                {
                    var array = data.FindProperty("m_SavedProperties.m_Floats"); var index = array.arraySize++;
                    var item = array.GetArrayElementAtIndex(index);
                    item.FindPropertyRelative("first").stringValue = "_AuthoredCustomSetting";
                    item.FindPropertyRelative("second").floatValue = 2;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var before = ExportSourceFingerprint.Compute(source);
                using (var data = new SerializedObject(material))
                {
                    var array = data.FindProperty("m_SavedProperties.m_Floats");
                    var item = Enumerable.Range(0, array.arraySize).Select(array.GetArrayElementAtIndex)
                        .Single(entry => entry.FindPropertyRelative("first").stringValue == "_AuthoredCustomSetting");
                    item.FindPropertyRelative("second").floatValue = 3;
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                Assert.That(ExportSourceFingerprint.Compute(source), Is.Not.EqualTo(before));
                before = ExportSourceFingerprint.Compute(source);
                material.SetTextureScale("_MainTex", new Vector2(2, 1));
                Assert.That(ExportSourceFingerprint.Compute(source), Is.Not.EqualTo(before));
                before = ExportSourceFingerprint.Compute(source);
                material.SetTextureOffset("_MainTex", new Vector2(.25f, 0));
                Assert.That(ExportSourceFingerprint.Compute(source), Is.Not.EqualTo(before));
                var serialized = EditorJsonUtility.ToJson(material);
                ExportSourceFingerprint.Compute(source);
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(serialized), "Fingerprinting never writes material defaults to the original.");
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(material); }
        }

        [Test]
        public void ColdClipEditorCacheDoesNotInvalidateButNativeCurveEditsDo()
        {
            var source = new GameObject("Source"); var controller = new AnimatorController();
            var clip = new AnimationClip(); AnimatorStateMachine machine = null; AnimatorState state = null;
            try
            {
                controller.AddLayer("FX"); machine = controller.layers[0].stateMachine;
                state = machine.AddState("Rest"); state.motion = clip; machine.defaultState = state;
                source.AddComponent<Animator>().runtimeAnimatorController = controller;
                var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), "Native parameter");
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 0, 1, 1));
                using (var data = new SerializedObject(clip))
                {
                    data.FindProperty("m_EditorCurves").ClearArray(); data.ApplyModifiedPropertiesWithoutUndo();
                }
                using (var data = new SerializedObject(clip))
                    Assert.That(data.FindProperty("m_FloatCurves").arraySize, Is.EqualTo(1), "Keep the runtime curve while dropping its lazily-built editor view.");
                var before = ExportSourceFingerprint.Compute(source);
                foreach (var item in AnimationUtility.GetCurveBindings(clip)) AnimationUtility.GetEditorCurve(clip, item);
                Assert.That(AnimationUtility.GetEditorCurve(clip, binding).Evaluate(.5f), Is.EqualTo(.5f).Within(.00001));
                Assert.That(ExportSourceFingerprint.Compute(source), Is.EqualTo(before));
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 0, 1, 2));
                Assert.That(ExportSourceFingerprint.Compute(source), Is.Not.EqualTo(before));
            }
            finally
            {
                Object.DestroyImmediate(source); Object.DestroyImmediate(state); Object.DestroyImmediate(machine);
                Object.DestroyImmediate(controller); Object.DestroyImmediate(clip);
            }
        }
    }
}
