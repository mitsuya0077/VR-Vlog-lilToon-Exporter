using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VRVlog.LilToonExporter.Compatibility;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VRVlog.LilToonExporter
{
    // Optional bridge: no NDMF/MA assembly dependency. Use NDMF's ordered passes
    // on an owned export copy, preserving authored merge/proxy semantics.
    internal sealed class NdmfExportPreparation : IDisposable
    {
        private readonly HashSet<Object> generated = new HashSet<Object>();
        private string temporaryAssetPath, temporaryAssetGuid;
        private const string MaNamespace = "nadena.dev.modular_avatar.core.";
        private const string CompatibilityMessage =
            "Modular Avatar の準備に必要な NDMF API を利用できません。NDMF " + DependencyPolicy.NdmfMinimum + " 以降の 1.x が必要です。確認済み構成: MA " + DependencyPolicy.ModularAvatarReference + " / NDMF " + DependencyPolicy.NdmfReference + "。" + DependencyPolicy.Recovery;

        internal static bool NeedsProcessing(GameObject avatar) => avatar != null && RelevantAuthoring(avatar).Count != 0;

        private static bool IsAuthoringTag(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.FullName == MaNamespace + "AvatarTagComponent") return true;
            return type.GetInterfaces().Any(i => i.FullName == "nadena.dev.ndmf.runtime.INDMFEditorOnly");
        }

        private static HashSet<Component> RelevantAuthoring(GameObject avatar, Func<Transform, bool> excluded = null)
        {
            var components = avatar.GetComponentsInChildren<Component>(true)
                .Where(component => component != null && excluded?.Invoke(component.transform) != true).ToArray();
            var tags = components.Where(component => IsAuthoringTag(component.GetType())).ToArray();
            if (tags.Length == 0) return new HashSet<Component>();
            var byTransform = components.GroupBy(component => component.transform).ToDictionary(group => group.Key, group => group.ToArray());
            var required = new HashSet<Transform>();
            var pending = new Queue<Object>();
            var visited = new HashSet<Object>();
            void Require(Transform target)
            {
                if (target == null || (target != avatar.transform && !target.IsChildOf(avatar.transform)) || excluded?.Invoke(target) == true) return;
                for (var node = target; node != null; node = node.parent)
                {
                    if (required.Add(node) && byTransform.TryGetValue(node, out var owners))
                        foreach (var owner in owners)
                            // Transform's children/parent are hierarchy structure,
                            // not a reason to process an unused inactive wardrobe.
                            if (!(owner is Transform) && (!(owner is Renderer renderer) || (renderer.enabled && renderer.gameObject.activeInHierarchy))) pending.Enqueue(owner);
                    if (node == avatar.transform) break;
                }
            }
            foreach (var component in components)
                if (component.gameObject.activeInHierarchy) Require(component.transform);
            while (pending.Count != 0)
            {
                var owner = pending.Dequeue();
                if (owner == null || !visited.Add(owner)) continue;
                if (owner is Component component)
                {
                    var property = FollowingProperty(component);
                    if (property != null) Require(ReadFollowingTarget(component, property));
                    if (component is SkinnedMeshRenderer skin)
                    {
                        Require(skin.rootBone);
                        foreach (var bone in skin.bones ?? Array.Empty<Transform>()) Require(bone);
                    }
                    if (component is Animator animator && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
                        for (var bone = 0; bone < (int)HumanBodyBones.LastBone; bone++) Require(animator.GetBoneTransform((HumanBodyBones)bone));
                }
                foreach (var reference in References(owner))
                {
                    if (reference is GameObject gameObject) Require(gameObject.transform);
                    else if (reference is Component dependency)
                    {
                        // LOD/settings components can reference unused renderers.
                        // Apply the same output visibility gate at this entry point.
                        if (dependency is Renderer renderer && (!renderer.enabled || !renderer.gameObject.activeInHierarchy)) continue;
                        Require(dependency.transform);
                    }
                    else if (IsMutableAsset(reference)) pending.Enqueue(reference);
                }
            }
            return new HashSet<Component>(tags.Where(component => required.Contains(component.transform)));
        }

        private static void PruneUnusedAuthoring(GameObject clone)
        {
            var retained = RelevantAuthoring(clone);
            foreach (var component in clone.GetComponentsInChildren<Component>(true))
                if (component != null && IsAuthoringTag(component.GetType()) && !retained.Contains(component))
                    Object.DestroyImmediate(component);
        }

        private static string FollowingProperty(Component component)
        {
            for (var type = component.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName == MaNamespace + "ModularAvatarMergeArmature") return "mergeTargetObject";
                if (type.FullName == MaNamespace + "ModularAvatarBoneProxy") return "target";
            }
            return null;
        }

        internal static Transform FollowingTarget(Component component)
        {
            var property = FollowingProperty(component);
            return property == null ? null : ReadFollowingTarget(component, property);
        }

        private static Transform ReadFollowingTarget(Component component, string property)
        {
            var getter = component.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            if (getter == null) throw new InvalidOperationException(CompatibilityMessage);
            var value = Invoke(() => getter.GetValue(component));
            return value is GameObject gameObject ? gameObject.transform : value as Transform;
        }

        internal static void ValidateSource(GameObject source, Func<Transform, bool> excluded = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            foreach (var component in RelevantAuthoring(source, excluded))
            {
                var property = FollowingProperty(component);
                if (property == null) continue;
                var target = ReadFollowingTarget(component, property);
                if (target == null)
                    throw new InvalidOperationException(component.name + ": Modular Avatar の追従先を取得できません。Merge Armature / Bone Proxy の対象を設定してから書き出してください。");
                if (target != source.transform && !target.IsChildOf(source.transform))
                    throw new InvalidOperationException(component.name + ": Modular Avatar の追従先が選択したアバターの外にあります。アバター全体を選ぶか、追従先を同じアバター内に設定してください。");
                if (excluded?.Invoke(target) == true)
                    throw new InvalidOperationException(component.name + ": Modular Avatar の追従先が書き出しの除外対象です。追従するオブジェクトも除外するか、除外設定を見直してください。");
            }
        }

        internal static NdmfExportPreparation Prepare(GameObject source, GameObject clone, ICollection<string> warnings = null)
        {
            RequireOwnedCopy(source, clone);
            if (!NeedsProcessing(clone))
            {
                PruneUnusedAuthoring(clone);
                return new NdmfExportPreparation();
            }
            ValidateSource(clone);
            var processor = FindType("nadena.dev.ndmf.AvatarProcessor");
            var package = processor != null ? PackageInfo.FindForAssembly(processor.Assembly) : null;
            var bridge = Bridge.Resolve(FindType, package?.version);
            return ProcessClone(source, clone, bridge, warnings);
        }

        internal static NdmfExportPreparation ProcessClone(GameObject source, GameObject clone, Bridge bridge,
            ICollection<string> warnings = null)
        {
            RequireOwnedCopy(source, clone);
            // NDMF processes inactive tags too. Remove only irrelevant tags on
            // our copy before dependency safety checks or any canonical pass.
            PruneUnusedAuthoring(clone);
            var lease = new NdmfExportPreparation();
            var sourceAssets = Dependencies(source);
            var cloneAssets = Dependencies(clone);
            var existing = new HashSet<Object>(sourceAssets);
            existing.UnionWith(cloneAssets);
            var requiredMorphs = ExportMorphs(clone);
            object context = null;
            IDisposable directoryScope = null;
            Exception processError = null;
            try
            {
                try
                {
                    RequireCopySceneReferences(clone, cloneAssets);
                    lease.IsolateSharedAssets(clone, sourceAssets, cloneAssets);
                    // Legacy NDMF plugins (including LightLimitChanger) add
                    // subassets directly to context.AssetContainer. A null
                    // root selects NullAssetSaver and breaks every such pass.
                    // Use an exclusively owned folder for this export lease.
                    lease.CreateTemporaryAssetDirectory();
                    directoryScope = (IDisposable)Invoke(() => bridge.DirectoryScope.Invoke(new object[] { lease.temporaryAssetPath }));
                    var platform = Invoke(() => bridge.PrimaryPlatform.Invoke(null, new object[] { clone })) ??
                        Invoke(() => bridge.GenericPlatform.GetValue(null));
                    if (platform == null) throw new InvalidOperationException(CompatibilityMessage);
                    context = Invoke(() => bridge.Context.Invoke(new object[] { clone, lease.temporaryAssetPath, platform, true }));
                    try
                    {
                        Invoke(() => bridge.Process.Invoke(null, new[] { context, bridge.First, bridge.Transforming }));
                    }
                    catch (Exception error) { processError = error; }
                    try { Invoke(() => bridge.Finish.Invoke(context, null)); }
                    catch (Exception error)
                    {
                        processError = processError == null ? error : new AggregateException(processError, error);
                    }
                    if (processError != null)
                        throw BuildFailure(context, processError);
                    // NDMF records plugin exceptions instead of always rethrowing.
                    if (!(Invoke(() => bridge.Successful.GetValue(context)) is bool success) || !success)
                        throw BuildFailure(context, null);
                    if (!requiredMorphs.IsSubsetOf(ExportMorphs(clone)))
                        throw new InvalidOperationException("Modular Avatar / NDMF の処理で書き出し用の表情が失われました。メッシュや BlendShape を変更する追加ツールの設定を確認してください。");
                    warnings?.Add("Modular Avatar / NDMF の衣装・追従設定を一時コピーに適用しました（最適化フェーズは実行していません）。");
                    return lease;
                }
                finally
                {
                    try
                    {
                        if (context != null && Invoke(() => bridge.AssetSaver.GetValue(context)) is IDisposable saver) saver.Dispose();
                    }
                    finally
                    {
                        try { directoryScope?.Dispose(); }
                        finally { lease.CaptureGenerated(clone, existing); }
                    }
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private static void RequireOwnedCopy(GameObject source, GameObject clone)
        {
            if (source == null || clone == null) throw new ArgumentNullException(source == null ? nameof(source) : nameof(clone));
            if (source == clone || clone.transform.IsChildOf(source.transform) || source.transform.IsChildOf(clone.transform) || EditorUtility.IsPersistent(clone))
                throw new InvalidOperationException("NDMF の準備には元のアバターから独立した書き出し用コピーが必要です。");
        }

        private static HashSet<Object> Dependencies(GameObject root) => Dependencies(root == null
            ? Array.Empty<Object>()
            : root.GetComponentsInChildren<Component>(true).Cast<Object>().Concat(new Object[] { root }));

        private static HashSet<Object> Dependencies(IEnumerable<Object> roots)
        {
            // CollectDependencies omits unsaved references in the native Editor.
            // Traverse the live serialized graph so copy ownership also covers
            // generated meshes and nested, unsaved authoring settings.
            var result = new HashSet<Object>();
            var pending = new Queue<Object>(roots);
            while (pending.Count > 0)
            {
                var value = pending.Dequeue();
                if (value == null || !result.Add(value) || value is Shader || value is ComputeShader || value is MonoScript) continue;
                foreach (var reference in References(value)) pending.Enqueue(reference);
            }
            return result;
        }

        private static bool IsMutableAsset(Object value) => value != null &&
            !(value is GameObject) && !(value is Component) && !(value is Shader) &&
            !(value is ComputeShader) && !(value is MonoScript);

        private static IEnumerable<Object> References(Object value)
        {
            // Mesh/texture payloads can contain millions of scalar elements,
            // but do not contain asset references that need remapping here.
            if (value is Mesh || value is Texture) yield break;
            using (var serialized = new SerializedObject(value))
            {
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null)
                        yield return property.objectReferenceValue;
            }
        }

        private static void RequireCopySceneReferences(GameObject clone, HashSet<Object> cloneAssets)
        {
            // Instantiate remaps scene references on cloned components, but a
            // shared ScriptableObject can still point into the original rig.
            // Do not guess a matching transform after exclusions or mesh edits.
            var holders = clone.GetComponentsInChildren<Component>(true).Where(value => value != null).Cast<Object>()
                .Concat(cloneAssets.Where(IsMutableAsset)).Distinct();
            foreach (var holder in holders)
                foreach (var reference in References(holder))
                {
                    var target = reference is GameObject gameObject ? gameObject.transform :
                        (reference as Component)?.transform;
                    if (target != null && !EditorUtility.IsPersistent(reference) && target != clone.transform && !target.IsChildOf(clone.transform))
                        throw new InvalidOperationException(holder.name + ": 設定に書き出し用コピーの外部を指すシーン参照が残っています。" +
                            "元のアバターやシーンを保護するため NDMF の処理を中止しました。該当設定の参照先を確認し、書き出し用コピー内で完結する構成にしてください。");
                }
        }

        private void IsolateSharedAssets(GameObject clone, HashSet<Object> sourceAssets, HashSet<Object> cloneAssets)
        {
            // NullAssetSaver treats every nonpersistent asset as mutable. A
            // scene avatar can legitimately own unsaved meshes, clips, menus,
            // materials or textures; Instantiate(GameObject) shares these.
            var candidates = cloneAssets.Where(IsMutableAsset).ToArray();
            var toCopy = new HashSet<Object>(candidates.Where(value => sourceAssets.Contains(value) && !EditorUtility.IsPersistent(value)));
            if (toCopy.Count == 0) return;
            var references = candidates.ToDictionary(value => value, value => References(value).ToArray());
            // A persistent container may currently reference an unsaved child.
            // Copy that container too, so redirecting the child never edits it.
            bool added;
            do
            {
                added = false;
                foreach (var value in candidates)
                    if (EditorUtility.IsPersistent(value) && !toCopy.Contains(value) && references[value].Any(toCopy.Contains))
                        added |= toCopy.Add(value);
            } while (added);
            var replacements = new Dictionary<Object, Object>();
            foreach (var value in toCopy)
            {
                Object copy;
                try { copy = Object.Instantiate(value); }
                catch (Exception error)
                {
                    throw new InvalidOperationException(value.name + ": 未保存のアバター素材を一時コピーに分離できません。素材をプロジェクト内のアセットとして保存してから書き出してください。", error);
                }
                if (copy == null || copy == value) throw new InvalidOperationException("アバター素材の一時コピーを作成できませんでした。");
                generated.Add(copy);
                copy.name = value.name;
                replacements.Add(value, copy);
            }
            var copiedDependencies = Dependencies(replacements.Values)
                .Where(value => IsMutableAsset(value) && !EditorUtility.IsPersistent(value) &&
                    !sourceAssets.Contains(value) && !cloneAssets.Contains(value)).ToArray();
            generated.UnionWith(copiedDependencies);
            var targets = clone.GetComponentsInChildren<Component>(true).Where(value => value != null).Cast<Object>()
                .Concat(replacements.Values).Concat(copiedDependencies)
                .Concat(cloneAssets.Where(value => IsMutableAsset(value) && !sourceAssets.Contains(value) && !EditorUtility.IsPersistent(value)))
                .Distinct();
            foreach (var target in targets)
            {
                if (target is Mesh || target is Texture) continue;
                using (var serialized = new SerializedObject(target))
                {
                    var property = serialized.GetIterator();
                    var changed = false;
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var original = property.objectReferenceValue;
                        if (original == null || !replacements.TryGetValue(original, out var replacement)) continue;
                        property.objectReferenceValue = replacement;
                        changed = true;
                    }
                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private void CaptureGenerated(GameObject clone, HashSet<Object> existing)
        {
            foreach (var value in Dependencies(clone))
                if (value != null && !(value is GameObject) && !(value is Component) &&
                    !EditorUtility.IsPersistent(value) && !existing.Contains(value)) generated.Add(value);
        }

        private static HashSet<string> ExportMorphs(GameObject root)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (root == null) return result;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                for (var index = 0; index < mesh.blendShapeCount; index++)
                {
                    var name = mesh.GetBlendShapeName(index);
                    if (name.StartsWith("__VRVlog_Menu_", StringComparison.Ordinal) ||
                        name.StartsWith("__VRVlog_Anim_", StringComparison.Ordinal)) result.Add(name);
                }
            }
            return result;
        }

        public void Dispose()
        {
            try
            {
                foreach (var value in generated)
                    if (value != null && !EditorUtility.IsPersistent(value)) Object.DestroyImmediate(value);
                generated.Clear();
            }
            finally
            {
                if (temporaryAssetPath != null)
                {
                    var path = temporaryAssetPath;
                    var guid = temporaryAssetGuid;
                    temporaryAssetPath = temporaryAssetGuid = null;
                    // Do not remove a replacement folder or a moved/user asset.
                    if (AssetDatabase.IsValidFolder(path) && AssetDatabase.AssetPathToGUID(path) == guid)
                    {
                        if (!AssetDatabase.DeleteAsset(path))
                            Debug.LogWarning("VR Vlog: 書き出しの一時データを削除できませんでした: " + path);
                    }
                }
            }
        }

        private void CreateTemporaryAssetDirectory()
        {
            var name = "VRVlogExportTemp-" + Guid.NewGuid().ToString("N");
            var guid = AssetDatabase.CreateFolder("Assets", name);
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(guid) || path != "Assets/" + name || !AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException("書き出し用の一時保存先を作成できませんでした。UnityプロジェクトのAssetsフォルダーへの書き込み権限と空き容量を確認してください。");
            temporaryAssetPath = path;
            temporaryAssetGuid = guid;
        }

        private static InvalidOperationException BuildFailure(object context, Exception cause)
        {
            var details = new List<string>();
            var exceptions = new List<Exception>();
            if (cause != null) exceptions.Add(cause);
            try
            {
                var report = ReadDiagnosticMember(context, "ErrorReport") ?? ReadDiagnosticMember(context, "_report");
                if (ReadDiagnosticMember(report, "Errors") is System.Collections.IEnumerable errors)
                    foreach (var entry in errors)
                    {
                        var error = ReadDiagnosticMember(entry, "TheError");
                        var severity = ReadDiagnosticMember(error, "Severity")?.ToString();
                        if (severity != "Error" && severity != "InternalError") continue;
                        var plugin = ReadDiagnosticMember(entry, "Plugin");
                        var pluginName = ReadDiagnosticMember(plugin, "DisplayName") as string ?? "NDMF";
                        var pass = ReadDiagnosticMember(entry, "PassName") as string;
                        var exception = ReadDiagnosticMember(error, "Exception") as Exception;
                        if (exception != null) exceptions.Add(exception);
                        var message = exception?.Message ?? error?.GetType().GetMethod("ToMessage", Type.EmptyTypes)?.Invoke(error, null) as string;
                        if (string.IsNullOrWhiteSpace(message)) message = "処理中にエラーが発生しました。";
                        var detail = pluginName + (string.IsNullOrWhiteSpace(pass) ? "" : " / " + pass) + ": " + message.Trim();
                        if (detail.Length > 900) detail = detail.Substring(0, 900) + "…";
                        if (!details.Contains(detail)) details.Add(detail);
                        if (details.Count >= 8) break;
                    }
            }
            catch { /* Diagnostics must not replace the original processing failure. */ }
            var summary = "書き出し用コピーの処理を完了できませんでした。元のアバターは変更していません。";
            if (details.Count > 0) summary += "\n\n" + string.Join("\n", details);
            else if (cause != null) summary += "\n\n" + cause.Message;
            summary += "\n\nNDMFコンソールの該当ツールを開くと、対象オブジェクトを確認できます。「詳細をコピー」で原因の情報もコピーできます。";
            var inner = exceptions.Count == 1 ? exceptions[0] : exceptions.Count > 1 ? new AggregateException(exceptions) : null;
            return new InvalidOperationException(summary, inner);
        }

        private static object ReadDiagnosticMember(object owner, string name)
        {
            if (owner == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = owner.GetType();
            return type.GetProperty(name, flags)?.GetValue(owner) ?? type.GetField(name, flags)?.GetValue(owner);
        }

        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        private static object Invoke(Func<object> operation)
        {
            try { return operation(); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        // The phase-limited entry point and Finish are internal in the official
        // 1.8.3, 1.13.0 and 1.14.8 sources. Validate the whole bridge before any
        // processing; public ProcessAvatar would also run mesh optimization.
        internal sealed class Bridge
        {
            internal ConstructorInfo Context, DirectoryScope;
            internal MethodInfo Process, Finish, PrimaryPlatform;
            internal PropertyInfo Successful, AssetSaver, GenericPlatform;
            internal object First, Transforming;

            internal static Bridge Resolve(Func<string, Type> find, string version)
            {
                const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
                const BindingFlags staticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                if (version == null || version.Contains("-") || !Version.TryParse(version, out var parsed) ||
                    parsed.Major != DependencyPolicy.NdmfMajor || parsed < Version.Parse(DependencyPolicy.NdmfMinimum)) throw new InvalidOperationException(CompatibilityMessage);
                var processor = find("nadena.dev.ndmf.AvatarProcessor");
                var context = find("nadena.dev.ndmf.BuildContext");
                var phase = find("nadena.dev.ndmf.BuildPhase");
                var provider = find("nadena.dev.ndmf.platform.INDMFPlatformProvider");
                var registry = find("nadena.dev.ndmf.platform.PlatformRegistry");
                var generic = find("nadena.dev.ndmf.platform.GenericPlatform");
                var directory = find("nadena.dev.ndmf.OverrideTemporaryDirectoryScope");
                if (new[] { processor, context, phase, provider, registry, generic, directory }.Any(t => t == null))
                    throw new InvalidOperationException(CompatibilityMessage);
                var result = new Bridge
                {
                    Context = context.GetConstructor(new[] { typeof(GameObject), typeof(string), provider, typeof(bool) }),
                    DirectoryScope = directory.GetConstructor(new[] { typeof(string) }),
                    Process = processor.GetMethod("ProcessAvatar", staticMembers, null, new[] { context, phase, phase }, null),
                    Finish = context.GetMethod("Finish", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null),
                    Successful = context.GetProperty("Successful", publicInstance),
                    AssetSaver = context.GetProperty("AssetSaver", publicInstance),
                    PrimaryPlatform = registry.GetMethod("GetPrimaryPlatformForAvatar", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null),
                    GenericPlatform = generic.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static),
                    First = phase.GetProperty("First", staticMembers)?.GetValue(null),
                    Transforming = phase.GetField("Transforming", staticMembers)?.GetValue(null)
                };
                if (result.Context == null || result.DirectoryScope == null || !typeof(IDisposable).IsAssignableFrom(directory) ||
                    result.Process?.ReturnType != typeof(void) || result.Finish?.ReturnType != typeof(void) ||
                    result.Successful?.PropertyType != typeof(bool) || result.Successful.GetMethod == null || result.AssetSaver?.GetMethod == null ||
                    result.PrimaryPlatform?.ReturnType != provider || result.GenericPlatform?.PropertyType != provider ||
                    !phase.IsInstanceOfType(result.First) || !phase.IsInstanceOfType(result.Transforming))
                    throw new InvalidOperationException(CompatibilityMessage);
                return result;
            }
        }
    }
}
