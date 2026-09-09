using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    public sealed class ExportGimmickOptions
    {
        public bool AutoExclude = true;
        public IEnumerable<GameObject> IncludedObjects = Array.Empty<GameObject>();
    }

    internal enum GimmickExclusionUnit { Review, Renderer, Hierarchy }

    internal sealed class ExportGimmickFinding
    {
        internal GameObject Target;
        internal Renderer Renderer;
        internal GimmickExclusionUnit Unit;
        internal string Reason;
    }

    // Author-declared fallback behavior and a verified prefab identity are
    // evidence; names, colors, primitive shapes and arbitrary scripts are not.
    internal static class ExportGimmickDetection
    {
        internal const string NadePrefabGuid = "491c3f399da5d064d9966982ddf0d191";
        const string NadeControllerGuid = "e285f7b13023d4744948a36807877425";
        static readonly HashSet<string> PortableShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Standard", "Standard (Specular setup)", "VRM/MToon", "VRM10/MToon10",
            "UniGLTF/UniUnlit", "Unlit/Color", "Unlit/Texture", "Unlit/Transparent",
            "Unlit/Transparent Cutout", "VRM/UnlitTexture", "VRM/UnlitCutout",
            "VRM/UnlitTransparent", "VRM/UnlitTransparentZWrite"
        };

        internal static bool Portable(Material material) => material != null && material.shader != null &&
            (LilToonMaterialReader.IsLilToon(material) || PortableShaders.Contains(material.shader.name));

        internal static bool HiddenFallback(Material material) => material != null && material.shader != null &&
            !Portable(material) && string.Equals(material.GetTag("VRCFallback", true, "").Trim(), "Hidden", StringComparison.OrdinalIgnoreCase);

        internal static ExportGimmickFinding Inspect(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return null;
            // Other renderer types have their own export semantics.
            var skin = renderer as SkinnedMeshRenderer;
            if (skin == null && !(renderer is MeshRenderer)) return null;
            var mesh = skin != null ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var materials = renderer.sharedMaterials;
            var finding = new ExportGimmickFinding { Target = renderer.gameObject, Renderer = renderer };
            if (mesh == null || mesh.subMeshCount == 0 || materials.Length < mesh.subMeshCount ||
                materials.Length == 0 || materials.Any(m => m == null || m.shader == null))
                finding.Reason = "メッシュまたは材質が不足しているため自動判定できません。";
            else if (materials.All(Portable)) return null;
            else if (materials.All(HiddenFallback))
            {
                finding.Unit = GimmickExclusionUnit.Renderer;
                finding.Reason = "未対応シェーダーに、代替表示では隠す指定があります（VRCFallback=Hidden）。";
            }
            else finding.Reason = materials.Any(HiddenFallback)
                ? "通常の材質と補助演出が混在しているため、そのまま含めます。"
                : "未対応シェーダーですが、非表示の指定がないため、そのまま含めます。";
            return finding;
        }

        internal static List<ExportGimmickFinding> Analyze(GameObject source, Func<Transform, bool> manuallyExcluded = null)
        {
            var result = new List<ExportGimmickFinding>();
            if (source == null) return result;
            bool Excluded(Transform t) => manuallyExcluded?.Invoke(t) == true;
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            {
                if (Excluded(renderer.transform)) continue;
                var finding = Inspect(renderer);
                if (finding != null) result.Add(finding);
            }
            foreach (var transform in source.GetComponentsInChildren<Transform>(true))
            {
                if (transform == source.transform || !transform.gameObject.activeInHierarchy || Excluded(transform) || !KnownNadeRoot(transform.gameObject)) continue;
                var reason = UnsafeRootReason(source, transform, Excluded);
                result.Insert(0, new ExportGimmickFinding
                {
                    Target = transform.gameObject,
                    Unit = reason == null ? GimmickExclusionUnit.Hierarchy : GimmickExclusionUnit.Review,
                    Reason = reason ?? "確認済みの撫でシステムです。影・音・接触判定をまとめて省略します。"
                });
            }
            return result;
        }

        internal static bool Kept(Transform target, IEnumerable<GameObject> included) => target != null &&
            (included ?? Array.Empty<GameObject>()).Any(go => go != null && (target == go.transform || target.IsChildOf(go.transform)));

        internal static IEnumerable<GameObject> AutomaticRoots(IEnumerable<ExportGimmickFinding> findings, ExportGimmickOptions options)
        {
            if (options?.AutoExclude == false) return Array.Empty<GameObject>();
            var kept = (options?.IncludedObjects ?? Array.Empty<GameObject>()).Where(go => go != null).ToArray();
            return findings.Where(f => f.Unit == GimmickExclusionUnit.Hierarchy && !Kept(f.Target.transform, kept) &&
                !kept.Any(go => go.transform.IsChildOf(f.Target.transform))).Select(f => f.Target).ToArray();
        }

        internal static bool KnownNadeRoot(GameObject target)
        {
            // Keep identity independent of renaming and installation folder.
            var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(target);
            if (original == null || AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(original)) != NadePrefabGuid) return false;
            return HasNadeStructure(target);
        }

        internal static bool HasNadeStructure(GameObject target)
        {
            var root = target.transform;
            return root.Find("HeadSystem") != null && root.Find("Proxies") != null &&
                HasContact(root.Find("RxHeadMain"), "NadeRxHeadMain") &&
                HasContact(root.Find("RightHandSystem/RxRight"), "NadeRxRightHand") &&
                HasContact(root.Find("LeftHandSystem/RxLeft"), "NadeRxLeftHand");
        }

        static bool HasContact(Transform target, string parameter)
        {
            if (target == null) return false;
            foreach (var component in target.GetComponents<Component>())
            {
                if (component == null || component.GetType().FullName != "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver") continue;
                using var serialized = new SerializedObject(component);
                var property = serialized.FindProperty("parameter");
                if (property != null && property.propertyType == SerializedPropertyType.String && property.stringValue == parameter) return true;
            }
            return false;
        }

        internal static string UnsafeRootReason(GameObject source, Transform root, Func<Transform, bool> manuallyExcluded)
        {
            bool Within(Transform t) => t != null && (t == root || t.IsChildOf(root));
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (manuallyExcluded(renderer.transform)) continue;
                // Inactive ordinary wardrobe parts are also evidence against
                // deleting the entire system, even though they aren't exported.
                var materials = renderer.sharedMaterials;
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || mesh.subMeshCount == 0 || materials.Length < mesh.subMeshCount || materials.Any(m => m == null || m.shader == null))
                    return "メッシュまたは材質が不足しているため、システム全体は残します。";
                if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) || materials.Length == 0 || !materials.All(HiddenFallback))
                    return "通常の表示物を含むため、システム全体は残して補助Rendererだけを判定します。";
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) return "不明なコンポーネントを含むため、システム全体は残します。";
                if (manuallyExcluded(component.transform)) continue;
                var name = component.GetType().Name;
                if (name == "ModularAvatarShapeChanger" || name == "ModularAvatarObjectToggle" ||
                    name == "ModularAvatarMaterialSetter" || name == "ModularAvatarMaterialSwap" || name == "ModularAvatarMeshCutter")
                    return "見た目を変更する設定を含むため、システム全体は残します。";
                if (!KnownNadeComponent(component)) return "未確認の設定が追加されているため、システム全体は残します。";
                if (name == "ModularAvatarMergeAnimator")
                {
                    using var serialized = new SerializedObject(component);
                    var mode = serialized.FindProperty("pathMode");
                    var reference = serialized.FindProperty("relativePathRoot.targetObject")?.objectReferenceValue as GameObject;
                    var path = serialized.FindProperty("relativePathRoot.referencePath")?.stringValue;
                    var controller = serialized.FindProperty("animator")?.objectReferenceValue as RuntimeAnimatorController;
                    if (mode == null || mode.intValue != 0 || reference != null && !Within(reference.transform) || !string.IsNullOrEmpty(path) || !KnownNadeController(controller))
                        return "システム外の見た目を変更する可能性があるため、システム全体は残します。";
                }
            }
            foreach (var animator in source.GetComponentsInChildren<Animator>(true))
                if (animator.isHuman)
                    for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
                        if (Within(animator.GetBoneTransform((HumanBodyBones)i)))
                            return "Humanoidの骨を含むため、システム全体は残します。";
            // Transform parent/children references express hierarchy ownership;
            // all other incoming component references must survive the export.
            foreach (var component in source.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || Within(component.transform) || manuallyExcluded(component.transform)) continue;
                // MA also stores connections as avatar-relative paths or
                // humanoid bone/subpath pairs, without an ObjectReference.
                try
                {
                    if (Within(NdmfExportPreparation.FollowingTarget(component)))
                        return "残す衣装のMA接続先を含むため、システム全体は残します。";
                }
                catch (Exception)
                {
                    return "MAの接続先を確認できないため、システム全体は残します。";
                }
                using var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    var target = value is GameObject go ? go.transform : (value as Component)?.transform;
                    if (Within(target)) return "残すオブジェクトから参照されているため、システム全体は残します。";
                }
            }
            return null;
        }

        static bool KnownNadeComponent(Component component)
        {
            var type = component.GetType();
            if (component is Animator animator) return animator.avatar == null && KnownNadeController(animator.runtimeAnimatorController);
            if (component is Transform || component is MeshFilter || component is MeshRenderer || component is SkinnedMeshRenderer ||
                component is AudioSource || component is Light || component is UnityEngine.Animations.IConstraint) return true;
            var fullName = type.FullName;
            if (fullName == "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver" ||
                fullName == "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone" ||
                fullName == "VRC.SDK3.Avatars.Components.VRCSpatialAudioSource") return true;
            return type.Namespace == "nadena.dev.modular_avatar.core" &&
                (type.Name == "ModularAvatarMergeAnimator" || type.Name == "ModularAvatarMenuInstaller" ||
                 type.Name == "ModularAvatarParameters" || type.Name == "ModularAvatarBoneProxy" || type.Name == "ModularAvatarMenuItem" ||
                 type.Name == "ModularAvatarConvertConstraints");
        }

        static bool KnownNadeController(RuntimeAnimatorController controller) => controller == null ||
            AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(controller)) == NadeControllerGuid;
    }
}
