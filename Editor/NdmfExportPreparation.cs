using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private const string MaNamespace = "nadena.dev.modular_avatar.core.";
        private const string CompatibilityMessage =
            "Modular Avatar の準備に必要な NDMF API を利用できません。ALCOM で NDMF 1.8.3 以降の 1.x と Modular Avatar を更新してから書き出してください。";

        internal static bool NeedsProcessing(GameObject avatar) => avatar != null &&
            avatar.GetComponentsInChildren<Component>(true).Any(component => component != null && IsAuthoringTag(component.GetType()));

        private static bool IsAuthoringTag(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.FullName == MaNamespace + "AvatarTagComponent") return true;
            return type.GetInterfaces().Any(i => i.FullName == "nadena.dev.ndmf.runtime.INDMFEditorOnly");
        }

        internal static void ValidateSource(GameObject source, Func<Transform, bool> excluded = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            foreach (var component in source.GetComponentsInChildren<Component>(true))
            {
                if (component == null || excluded?.Invoke(component.transform) == true) continue;
                string property = null;
                for (var type = component.GetType(); type != null; type = type.BaseType)
                {
                    if (type.FullName == MaNamespace + "ModularAvatarMergeArmature") property = "mergeTargetObject";
                    if (type.FullName == MaNamespace + "ModularAvatarBoneProxy") property = "target";
                }
                if (property == null) continue;
                var getter = component.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
                if (getter == null) throw new InvalidOperationException(CompatibilityMessage);
                var value = Invoke(() => getter.GetValue(component));
                var target = value is GameObject gameObject ? gameObject.transform : value as Transform;
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
            if (!NeedsProcessing(clone)) return new NdmfExportPreparation();
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
                    directoryScope = (IDisposable)Invoke(() => bridge.DirectoryScope.Invoke(new object[] { null }));
                    var platform = Invoke(() => bridge.PrimaryPlatform.Invoke(null, new object[] { clone })) ??
                        Invoke(() => bridge.GenericPlatform.GetValue(null));
                    if (platform == null) throw new InvalidOperationException(CompatibilityMessage);
                    context = Invoke(() => bridge.Context.Invoke(new object[] { clone, null, platform, true }));
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
                        throw new InvalidOperationException("Modular Avatar / NDMF の準備に失敗しました。NDMF のエラー表示を確認してください。", processError);
                    // NDMF records plugin exceptions instead of always rethrowing.
                    if (!(Invoke(() => bridge.Successful.GetValue(context)) is bool success) || !success)
                        throw new InvalidOperationException("Modular Avatar / NDMF がエラーを報告したため書き出しを中止しました。NDMF のエラー表示を確認して修正してください。");
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

        private static HashSet<Object> Dependencies(GameObject root) => root == null
            ? new HashSet<Object>()
            : new HashSet<Object>(EditorUtility.CollectDependencies(new Object[] { root }));

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
            var copiedDependencies = EditorUtility.CollectDependencies(replacements.Values.ToArray())
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
            foreach (var value in generated)
                if (value != null && !EditorUtility.IsPersistent(value)) Object.DestroyImmediate(value);
            generated.Clear();
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
                    parsed.Major != 1 || parsed < new Version(1, 8, 3)) throw new InvalidOperationException(CompatibilityMessage);
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
