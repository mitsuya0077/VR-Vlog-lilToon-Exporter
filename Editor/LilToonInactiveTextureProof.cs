using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRVlog.LilToon;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // A narrow proof for unsupported remnants in disabled main-color layers.
    // This never changes a material or treats a failed capture as permission
    // to omit a visible texture.
    internal sealed class LilToonInactiveTextureProof
    {
        readonly GameObject[] roots;
        bool? hasDynamicMaterialInputs;

        internal LilToonInactiveTextureProof(params GameObject[] roots)
        {
            this.roots = roots.Where(root => root != null).Distinct().ToArray();
        }

        internal bool CanOmit(Material material, string property)
        {
            if (material == null || material.shader == null || roots.Length == 0 ||
                !LilToon234Catalogue.Shaders.TryGetValue(material.shader.name, out var family)) return false;
            string layer;
            switch (property)
            {
                case "_Main2ndTex": case "_Main2ndBlendMask": case "_Main2ndDissolveMask": case "_Main2ndDissolveNoiseMask":
                    layer = "2nd"; break;
                case "_Main3rdTex": case "_Main3rdBlendMask": case "_Main3rdDissolveMask": case "_Main3rdDissolveNoiseMask":
                    layer = "3rd"; break;
                default: return false;
            }
            var enabled = "_UseMain" + layer + "Tex";
            if (!material.HasProperty(enabled) || material.GetFloat(enabled) != 0f) return false;
            // lilToonMulti replaces the runtime bool with true and selects the
            // layer through lil_replace_keywords.hlsl. A stale enabled keyword
            // must not be mistaken for an inactive layer merely from its float.
            if (family.StartsWith("ltsmulti", StringComparison.Ordinal) &&
                material.IsKeywordEnabled(layer == "2nd" ? "_COLORADDSUBDIFF_ON" : "_COLORCOLOR_ON")) return false;
            if (!hasDynamicMaterialInputs.HasValue) hasDynamicMaterialInputs = HasDynamicMaterialInputs();
            return !hasDynamicMaterialInputs.Value;
        }

        bool HasDynamicMaterialInputs()
        {
            try
            {
                // Check both the prepared avatar and its source. Build passes
                // may consume original controllers; native dependency lookup
                // omits unsaved graphs, so follow live serialized references.
                var components = roots.SelectMany(root => root.GetComponentsInChildren<Component>(true)).ToArray();
                if (components.Any(component => component == null)) return true;
                if (components.OfType<Renderer>().Any(renderer => renderer.HasPropertyBlock())) return true;
                var seen = new HashSet<Object>();
                var pending = new Queue<Object>(components.Cast<Object>());
                while (pending.Count > 0)
                {
                    var value = pending.Dequeue();
                    if (value == null || !seen.Add(value) || value is Material || value is Mesh || value is Texture ||
                        value is Shader || value is ComputeShader || value is MonoScript) continue;
                    // Custom state callbacks/events can change material state
                    // without an animation binding. Do not guess their effect.
                    if (value is StateMachineBehaviour) return true;
                    if (value is AnimationClip clip)
                    {
                        if (clip.events.Length != 0 || AnimationUtility.GetCurveBindings(clip).Any(MaterialBinding) ||
                            AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(MaterialBinding)) return true;
                        continue;
                    }
                    using (var serialized = new SerializedObject(value))
                    {
                        var property = serialized.GetIterator();
                        while (property.Next(true))
                        {
                            if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                            var reference = property.objectReferenceValue;
                            if (reference != null && !(reference is GameObject) && !(reference is Component)) pending.Enqueue(reference);
                        }
                    }
                }
                return false;
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is UnityException)
            {
                // An unreadable graph retains the original unsupported-texture
                // diagnostic; it cannot establish that an image is unused.
                return true;
            }
        }

        static bool MaterialBinding(EditorCurveBinding binding) =>
            binding.type != null && (typeof(Material).IsAssignableFrom(binding.type) || typeof(Shader).IsAssignableFrom(binding.type)) ||
            binding.propertyName.StartsWith("material.", StringComparison.Ordinal) ||
            binding.propertyName.StartsWith("materials.", StringComparison.Ordinal) ||
            binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal);
    }
}
