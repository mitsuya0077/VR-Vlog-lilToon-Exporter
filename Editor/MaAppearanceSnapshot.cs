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
                // Simulator selections describe the appearance to freeze, not
                // the complete set of states a menu can select. Analyze the
                // unforced copy separately so selecting a menu in SceneView
                // cannot erase its other expressions before MA generates FX.
                var authored = analyzerType.GetConstructor(new[] { contextType }).Invoke(new[] { context });
                analyzerType.GetField("OptimizeShapes", Members).SetValue(authored, false);
                var authoredAnalysis = analyzerType.GetMethod("Analyze").Invoke(authored, new object[] { clone });
                var dynamic = DynamicProperties(clone, (IDictionary)Field(authoredAnalysis, "Shapes"));
                var retained = new HashSet<Component>();
                foreach (DictionaryEntry shape in (IDictionary)Field(authoredAnalysis, "Shapes"))
                    if (dynamic.Contains(Property(shape.Key)))
                        foreach (var rule in (IEnumerable)Field(shape.Value, "actionGroups"))
                            if (Field(rule, "ControllingObject") is Component controller) retained.Add(controller);
                var selectorType = Required(Ma + "editor.IMeshSelector");
                var selectors = new Dictionary<SkinnedMeshRenderer, IList>();
                // Freeze irreversible geometry and constant appearance using
                // MA's own resolved simulator selection. Reversible reactions
                // stay authored so the canonical build can generate their FX.
                foreach (DictionaryEntry state in states)
                {
                    var original = (Object)Field(state.Key, "TargetObject");
                    if (original == null) continue;
                    var target = original;
                    if (!analyzeClone && !map.TryGetValue(original, out target)) continue;
                    var name = (string)Field(state.Key, "PropertyName");
                    // The original weights/material/visibility are the off
                    // endpoint from which MA generates reversible reactions.
                    // Writing the currently selected pose here would turn both
                    // endpoints into the same expression.
                    if (dynamic.Contains((target, name))) continue;
                    if (target is GameObject go && name == "m_IsActive" && state.Value is float active)
                        go.SetActive(active > .5f);
                    else if (target is SkinnedMeshRenderer skin && name.StartsWith("blendShape.", StringComparison.Ordinal) && state.Value is float weight)
                    {
                        var index = skin.sharedMesh == null ? -1 : skin.sharedMesh.GetBlendShapeIndex(name.Substring(11));
                        if (index >= 0) skin.SetBlendShapeWeight(index, Mathf.Clamp(weight, 0, 100));
                    }
                    else if (target is Renderer renderer && name.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) && state.Value is Material replacement)
                    {
                        var index = int.Parse(name.Substring(23).TrimEnd(']'));
                        var slots = renderer.sharedMaterials;
                        if (index >= 0 && index < slots.Length) { slots[index] = replacement; renderer.sharedMaterials = slots; }
                    }
                    else if (target is SkinnedMeshRenderer cut && state.Value != null && selectorType.IsInstanceOfType(state.Value))
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
                // Frozen operations must not generate animation or delete
                // morphs again during the following NDMF build passes.
                foreach (var component in components)
                {
                    if (retained.Contains(component))
                    {
                        if (component.GetType().Name != "ModularAvatarShapeChanger") continue;
                        // Delete is an irreversible geometry snapshot. A mixed
                        // component can still generate its independent Set rules.
                        var shapes = (IList)component.GetType().GetProperty("Shapes").GetValue(component);
                        for (var i = shapes.Count - 1; i >= 0; i--)
                            if (Field(shapes[i], "ChangeType").ToString() == "Delete") shapes.RemoveAt(i);
                        if (shapes.Count != 0) continue;
                    }
                    if (component.GetType().Name == "ModularAvatarMeshCutter")
                        foreach (var filter in component.GetComponents<Component>())
                            if (filter != null && filter.GetType().GetInterfaces().Any(i => i.FullName == Ma + "vertex_filters.IMeshSelectorBehavior"))
                                Object.DestroyImmediate(filter);
                    Object.DestroyImmediate(component);
                }
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Modular Avatar の現在の表示を固定できませんでした。MA " + Compatibility.DependencyPolicy.ModularAvatarReference + " / NDMF " + Compatibility.DependencyPolicy.NdmfReference + " で確認済みのプレビュー API が必要です。ALCOM でパッケージを確認してください。原本は変更していません。", error);
            }
        }

        static (Object Target, string Name) Property(object key) =>
            ((Object)Field(key, "TargetObject"), (string)Field(key, "PropertyName"));

        // Let MA describe dependencies between reactions. Menu conditions and
        // authored activation curves can vary; object toggles propagate that
        // variability to any reaction controlled by the affected hierarchy.
        static HashSet<(Object Target, string Name)> DynamicProperties(GameObject root, IDictionary properties)
        {
            var animated = AnimatedActivationObjects(root);
            var result = new HashSet<(Object, string)>();
            bool changed;
            do
            {
                changed = false;
                foreach (DictionaryEntry property in properties)
                {
                    var key = Property(property.Key);
                    if (key.Name.StartsWith("deletedShape.", StringComparison.Ordinal) || result.Contains(key)) continue;
                    foreach (var rule in (IEnumerable)Field(property.Value, "actionGroups"))
                        foreach (var condition in (IEnumerable)Field(rule, "ControllingConditions"))
                        {
                            var reference = Field(condition, "DebugReference") as Component;
                            var gameObject = Field(condition, "ReferenceObject") as GameObject;
                            var menu = reference != null && reference.GetType().FullName == Ma + "ModularAvatarMenuItem";
                            if (menu || gameObject != null && (animated.Contains(gameObject) || result.Contains((gameObject, "m_IsActive"))))
                                changed |= result.Add(key);
                        }
                }
            } while (changed);
            return result;
        }

        static HashSet<GameObject> AnimatedActivationObjects(GameObject root)
        {
            var result = new HashSet<GameObject>();
            void Collect(AnimationClip clip, Transform basis)
            {
                if (clip == null || basis == null) return;
                foreach (var binding in UnityEditor.AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(GameObject) || binding.propertyName != "m_IsActive") continue;
                    // Duplicate paths remain the build/sampler's diagnostic;
                    // retain every possible dependency instead of freezing one.
                    foreach (var node in basis.GetComponentsInChildren<Transform>(true))
                        if (UnityEditor.AnimationUtility.CalculateTransformPath(node, basis) == binding.path)
                            result.Add(node.gameObject);
                }
            }
            void CollectMotion(Motion motion, Transform basis)
            {
                // Merge Motion stores a Motion asset rather than a controller.
                // SerializedObject does not descend into referenced BlendTree
                // assets, so visit the complete authored graph explicitly.
                var pending = new Stack<Motion>();
                var visited = new HashSet<Motion>();
                if (motion != null) pending.Push(motion);
                while (pending.Count != 0)
                {
                    var current = pending.Pop();
                    if (current == null || !visited.Add(current)) continue;
                    if (visited.Count > 8192)
                        throw new InvalidOperationException("Modular Avatar activation Motion graph exceeds the supported analysis limit.");
                    if (current is AnimationClip clip) Collect(clip, basis);
                    else if (current is UnityEditor.Animations.BlendTree tree)
                        foreach (var child in tree.children)
                            if (child.motion != null) pending.Push(child.motion);
                }
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true).Where(value => value != null))
            {
                var basis = root.transform;
                if (component is Animator animator) basis = animator.transform;
                else
                {
                    var type = component.GetType();
                    var animatorMerge = type.FullName == Ma + "ModularAvatarMergeAnimator";
                    var motionMerge = type.FullName == Ma + "ModularAvatarMergeBlendTree";
                    if ((animatorMerge || motionMerge) &&
                        type.GetField(animatorMerge ? "pathMode" : "PathMode").GetValue(component).ToString() == "Relative")
                    {
                        var reference = type.GetField(animatorMerge ? "relativePathRoot" : "RelativePathRoot").GetValue(component);
                        // MA resolves both relative roots against the build's
                        // avatar root, even when the component is nested under
                        // another root marker, then falls back to its own object.
                        var target = reference?.GetType().GetMethod("Get", new[] { typeof(Component) })
                            .Invoke(reference, new object[] { root.transform }) as GameObject;
                        basis = target == null ? component.transform : target.transform;
                    }
                }
                using (var serialized = new UnityEditor.SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != UnityEditor.SerializedPropertyType.ObjectReference) continue;
                        if (property.objectReferenceValue is RuntimeAnimatorController controller)
                            foreach (var clip in controller.animationClips) Collect(clip, basis);
                        else if (property.objectReferenceValue is Motion motion) CollectMotion(motion, basis);
                    }
                }
            }
            return result;
        }

        static Dictionary<string, string> ParameterMap(object analyzer, Dictionary<Object, Object> objects)
        {
            var result = new Dictionary<string, string>();
            var getter = analyzer.GetType().GetMethod("GetGameObjectStateProperty")
                ?? throw new MissingMethodException(analyzer.GetType().FullName, "GetGameObjectStateProperty");
            var menuItemType = Required(Ma + "ModularAvatarMenuItem");
            MethodInfo assign = null;
            string MenuParameter(Object item)
            {
                if (item == null) return null;
                // Use the same read-only simulation path as MA's simulator UI.
                // Auto parameters contain the component identity, so they must
                // be remapped along with the selected MenuItem object.
                assign = assign ?? Required(Ma + "editor.ParameterAssignerPass").GetMethod("AssignMenuItemParameter", Members,
                    null, new[] { menuItemType, typeof(Dictionary<string, float>), typeof(IDictionary<,>).MakeGenericType(typeof(string), menuItemType), typeof(bool?) }, null)
                    ?? throw new MissingMethodException("ParameterAssignerPass.AssignMenuItemParameter");
                var condition = assign.Invoke(null, new object[] { item, null, null, true });
                return condition == null ? null : (string)Field(condition, "Parameter");
            }
            void Add(string before, string after)
            {
                if (before == null) return;
                // Shared named parameters stay valid for surviving controls.
                if (!result.ContainsKey(before) || after != null) result[before] = after;
            }
            foreach (var pair in objects)
            {
                if (pair.Key is GameObject from)
                    Add((string)getter.Invoke(analyzer, new object[] { from }),
                        pair.Value == null ? null : (string)getter.Invoke(analyzer, new object[] { pair.Value }));
                else if (menuItemType.IsInstanceOfType(pair.Key)) Add(MenuParameter(pair.Key), MenuParameter(pair.Value));
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
