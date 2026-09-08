using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Optional, API-checked bridge to MA's own preview evaluation. Do not
    // implement a second menu/condition evaluator or write into the source.
    internal static class MaAppearanceSnapshot
    {
        const string Ma = "nadena.dev.modular_avatar.core.";
        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        static readonly HashSet<string> Frozen = new HashSet<string> {
            "ModularAvatarShapeChanger", "ModularAvatarObjectToggle", "ModularAvatarMaterialSetter",
            "ModularAvatarMaterialSwap", "ModularAvatarMeshCutter"
        };

        public static void Apply(GameObject source, GameObject clone, ICollection<Mesh> owned,
            ExportObjectExclusions exclusions = null, ICollection<string> warnings = null)
        {
            var components = clone.GetComponentsInChildren<Component>(true).Where(c => c != null &&
                c.GetType().Namespace == Ma.TrimEnd('.') && Frozen.Contains(c.GetType().Name)).ToArray();
            var map = new Dictionary<Object, Object>();
            if (components.Length > 0) Map(source.transform, clone.transform, map);
            // Excluded authoring rules must not affect retained body/clothing.
            // Evaluate the pruned copy when exclusions are present; preserve
            // the simulator's object-based parameters through the identity map.
            exclusions?.Apply(clone, warnings);
            components = components.Where(c => c != null).ToArray();
            if (components.Length == 0) return;
            try
            {
                var contextType = Required("nadena.dev.ndmf.preview.ComputeContext");
                var context = contextType.GetProperty("NullContext", Members)?.GetValue(null)
                    ?? contextType.GetField("NullContext", Members)?.GetValue(null);
                if (context == null) throw new MissingMemberException("ComputeContext.NullContext");
                var analyzerType = Required(Ma + "editor.ReactiveObjectAnalyzer");
                var analyzer = analyzerType.GetConstructor(new[] { contextType }).Invoke(new[] { context });
                var analyzeClone = exclusions?.HasAny == true;
                var parameterMap = analyzeClone ? ParameterMap(analyzer, map) : null;
                // Fresh analysis for every export; cached SceneView analysis can
                // still describe the previous frame after an Inspector edit.
                var simulator = Required(Ma + "editor.Simulator.ROSimulator");
                CopyOverride(simulator, analyzer, "PropertyOverrides", "ForcePropertyOverrides", parameterMap, analyzeClone ? map : null);
                CopyOverride(simulator, analyzer, "MenuItemOverrides", "ForceMenuItems", parameterMap, analyzeClone ? map : null);
                var analysis = analyzerType.GetMethod("Analyze").Invoke(analyzer, new object[] { analyzeClone ? clone : source });
                var states = (IDictionary)Field(analysis, "InitialStates");
                var selectorType = Required(Ma + "editor.IMeshSelector");
                var selectors = new Dictionary<SkinnedMeshRenderer, IList>();
                bool Enabled(string name) => PreviewEnabled(source, context, name);
                var shape = Enabled("ShapeChangerPreview");
                var material = Enabled("MaterialSetterPreview");
                var visibility = Enabled("ObjectSwitcherPreview");
                var cutter = Enabled("MeshDeleterPreview");
                foreach (DictionaryEntry state in states)
                {
                    var original = (Object)Field(state.Key, "TargetObject");
                    if (original == null) continue;
                    var target = original;
                    if (!analyzeClone && !map.TryGetValue(original, out target)) continue;
                    var name = (string)Field(state.Key, "PropertyName");
                    if (visibility && target is GameObject go && name == "m_IsActive" && state.Value is float active)
                        go.SetActive(active > .5f);
                    else if (shape && target is SkinnedMeshRenderer skin && name.StartsWith("blendShape.", StringComparison.Ordinal) && state.Value is float weight)
                    {
                        var index = skin.sharedMesh == null ? -1 : skin.sharedMesh.GetBlendShapeIndex(name.Substring(11));
                        if (index >= 0) skin.SetBlendShapeWeight(index, Mathf.Clamp(weight, 0, 100));
                    }
                    else if (material && target is Renderer renderer && name.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) && state.Value is Material replacement)
                    {
                        var index = int.Parse(name.Substring(23).TrimEnd(']'));
                        var slots = renderer.sharedMaterials;
                        if (index >= 0 && index < slots.Length) { slots[index] = replacement; renderer.sharedMaterials = slots; }
                    }
                    else if (cutter && target is SkinnedMeshRenderer cut && state.Value != null && selectorType.IsInstanceOfType(state.Value))
                    {
                        if (!selectors.TryGetValue(cut, out var list))
                            selectors.Add(cut, list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(selectorType)));
                        list.Add(state.Value);
                    }
                }
                foreach (var pair in selectors)
                {
                    if (pair.Key.sharedMesh == null) continue;
                    var mesh = (Mesh)Required(Ma + "editor.RemoveVerticesFromMesh").GetMethod("FilterPrimitivesOnly", Members)
                        .Invoke(null, new object[] { pair.Key, pair.Key.sharedMesh, pair.Value });
                    owned.Add(mesh);
                    pair.Key.sharedMesh = mesh;
                }
                // The frozen operations must not generate animation or delete
                // morphs again during the following NDMF build passes.
                foreach (var component in components)
                {
                    if (component.GetType().Name == "ModularAvatarMeshCutter")
                        foreach (var filter in component.GetComponents<Component>())
                            if (filter != null && filter.GetType().GetInterfaces().Any(i => i.FullName == Ma + "vertex_filters.IMeshSelectorBehavior"))
                                Object.DestroyImmediate(filter);
                    Object.DestroyImmediate(component);
                }
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Modular Avatar の現在の表示を固定できませんでした。MA 1.18.7 / NDMF 1.14.8 で確認済みのプレビュー API が必要です。ALCOM でパッケージを確認してください。原本は変更していません。", error);
            }
        }

        static bool PreviewEnabled(GameObject root, object context, string name)
        {
            var preview = Required("nadena.dev.ndmf.preview.NDMFPreview");
            if (!(bool)preview.GetProperty("EnablePreviewsUI", Members).GetValue(null) ||
                (int)preview.GetProperty("DisablePreviewDepth", Members).GetValue(null) != 0 ||
                (bool)preview.GetMethod("IsExcludedFromDefaultPreview", Members).Invoke(null, new object[] { root })) return false;
            var prefsType = Required("nadena.dev.ndmf.preview.UI.PreviewPrefs");
            var prefs = prefsType.GetProperty("instance", Members).GetValue(null);
            if (!(bool)prefsType.GetMethod("IsPreviewPluginEnabled").Invoke(prefs, new object[] { "nadena.dev.modular-avatar" })) return false;
            var filter = Activator.CreateInstance(Required(Ma + "editor." + name), true);
            return (bool)filter.GetType().GetMethod("IsEnabled").Invoke(filter, new[] { context });
        }

        static Dictionary<string, string> ParameterMap(object analyzer, Dictionary<Object, Object> objects)
        {
            var result = new Dictionary<string, string>();
            foreach (var pair in objects)
            {
                if (!(pair.Key is GameObject from)) continue;
                foreach (var method in new[] { "GetGameObjectStateProperty", "GetMenuItemProperty" })
                {
                    var getter = analyzer.GetType().GetMethod(method);
                    var before = (string)getter.Invoke(analyzer, new object[] { from });
                    if (before == null) continue;
                    var after = pair.Value == null ? null : (string)getter.Invoke(analyzer, new object[] { pair.Value });
                    // Shared named parameters stay valid for surviving controls.
                    if (!result.ContainsKey(before) || after != null) result[before] = after;
                }
            }
            return result;
        }

        static void CopyOverride(Type simulator, object analyzer, string field, string property,
            Dictionary<string, string> parameters, Dictionary<Object, Object> objects)
        {
            var published = simulator.GetField(field, Members).GetValue(null);
            var value = published.GetType().GetProperty("Value").GetValue(published);
            if (value == null) return;
            if (parameters != null)
            {
                var mapped = value.GetType().GetMethod("Clear").Invoke(value, null);
                var set = value.GetType().GetMethod("SetItem");
                foreach (var item in (IEnumerable)value)
                {
                    var key = (string)item.GetType().GetProperty("Key").GetValue(item);
                    var entry = item.GetType().GetProperty("Value").GetValue(item);
                    if (parameters.TryGetValue(key, out var replacement)) key = replacement;
                    if (key == null) continue;
                    if (entry is Object original && objects.TryGetValue(original, out var copy))
                    { if (copy == null) continue; entry = copy; }
                    mapped = set.Invoke(mapped, new[] { key, entry });
                }
                value = mapped;
            }
            analyzer.GetType().GetProperty(property).SetValue(analyzer, value);
        }
        static object Field(object obj, string name) => obj.GetType().GetField(name, Members).GetValue(obj);
        static Type Required(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false))
            .FirstOrDefault(t => t != null) ?? throw new TypeLoadException(name);
        static void Map(Transform source, Transform clone, IDictionary<Object, Object> map)
        {
            map.Add(source.gameObject, clone.gameObject);
            var from = source.GetComponents<Component>(); var to = clone.GetComponents<Component>();
            for (var i = 0; i < from.Length; i++) if (from[i] != null && to[i] != null) map.Add(from[i], to[i]);
            for (var i = 0; i < source.childCount; i++) Map(source.GetChild(i), clone.GetChild(i), map);
        }
    }
}
