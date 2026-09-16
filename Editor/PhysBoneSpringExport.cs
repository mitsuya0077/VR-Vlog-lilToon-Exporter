using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UniGLTF.SpringBoneJobs;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Optional SDK bridge. Only the owned, post-NDMF export copy is changed.
    internal static class PhysBoneSpringExport
    {
        internal const string PhysBoneType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
        const string ColliderType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";

        internal sealed class Result
        {
            internal int Sources, Converted, Chains, Joints, Colliders, Skipped;
            internal int ExistingChains, ExistingJoints, ExistingColliders;
        }

        internal static bool IsPhysBone(Component component) => component != null && component.GetType().FullName == PhysBoneType;
        static bool Active(Component component) => component != null && component.gameObject.activeInHierarchy &&
            (!(component is Behaviour behaviour) || behaviour.enabled);

        // Do not let a removed explicit root become the SDK's implicit root.
        internal static void PruneRemovedRoots(GameObject copy, Func<Transform, bool> removed, ICollection<string> warnings)
        {
            foreach (var component in copy.GetComponentsInChildren<Component>(true).Where(IsPhysBone).ToArray())
            {
                var root = Read<Transform>(component, "rootTransform", null);
                if (root == null || !removed(root) || removed(component.transform)) continue;
                warnings?.Add(component.name + ": 除外したボーンを対象とするPhysBoneを省略しました。");
                Object.DestroyImmediate(component);
            }
        }

        internal static Result Convert(GameObject source, GameObject copy, ICollection<string> warnings)
        {
            if (source == null || copy == null || source == copy || copy.transform.IsChildOf(source.transform) ||
                source.transform.IsChildOf(copy.transform) || EditorUtility.IsPersistent(copy))
                throw new InvalidOperationException("揺れ物変換には元アバターから独立した一時コピーが必要です。");
            var components = copy.GetComponentsInChildren<Component>(true).Where(IsPhysBone).ToArray();
            var result = new Result { Sources = components.Length };
            if (components.Length == 0) return result;
            var instance = copy.GetComponent<Vrm10Instance>() ?? copy.AddComponent<Vrm10Instance>();
            var authored = instance.SpringBone.Springs.Where(s => s != null).ToArray();
            result.ExistingChains = authored.Length;
            result.ExistingJoints = authored.Sum(s => Math.Max(0, s.Joints.Count - 1));
            result.ExistingColliders = copy.GetComponentsInChildren<VRM10SpringBoneCollider>().Length;
            // Terminal components also contain authored fields and must not be overwritten.
            var occupied = new HashSet<Transform>(authored.SelectMany(s => s.Joints).Where(j => j != null).Select(j => j.transform));
            var existing = new HashSet<Transform>(occupied);
            var roots = components.Where(Active).ToDictionary(c => c, c => Root(c));
            var colliders = new Dictionary<Component, VRM10SpringBoneCollider>();
            foreach (var component in components)
            {
                if (!Active(component)) { Skip(result, warnings, component, "無効または非表示"); continue; }
                var root = roots[component];
                RequireLocal(copy, root);
                if (!root.gameObject.activeInHierarchy) { Skip(result, warnings, component, "Root Transformが非表示"); continue; }
                if (existing.Contains(root)) { Skip(result, warnings, component, "既存のVRM揺れ設定を優先"); continue; }
                var ignored = new HashSet<Transform>(Items(Read<object>(component, "ignoreTransforms", null)).OfType<Transform>());
                if (Read(component, "ignoreOtherPhysBones", true))
                    ignored.UnionWith(roots.Where(p => p.Key != component && p.Value != root).Select(p => p.Value));
                var endpoint = Vector(component, "endpointPosition", Vector3.zero);
                var multi = EnumNumber(component, "multiChildType", 0);
                if (multi < 0 || multi > 2) throw Unsupported(component, "Multi-Child Type");
                var chains = new List<List<Transform>>();
                var depths = new Dictionary<Transform, int>();
                var maximumDepth = 0;
                void Add(List<Transform> chain) { if (chain.Count >= 2) chains.Add(chain); }
                Transform Tip(Transform parent, Vector3 offset)
                {
                    var tip = new GameObject("VRVlog Spring Endpoint").transform;
                    tip.SetParent(parent, false); tip.localPosition = offset;
                    return tip;
                }
                void Walk(Transform node, List<Transform> path, int depth)
                {
                    if (depth > 256) throw Unsupported(component, "256段を超えるボーン階層");
                    depths[node] = depth; maximumDepth = Math.Max(maximumDepth, depth);
                    path.Add(node);
                    var children = node.Cast<Transform>().Where(t => t.gameObject.activeInHierarchy && !ignored.Contains(t)).ToArray();
                    if (children.Length == 0)
                    {
                        if (endpoint.sqrMagnitude > 1e-12f) { path.Add(Tip(node, endpoint)); maximumDepth = Math.Max(maximumDepth, depth + 1); }
                        Add(path); return;
                    }
                    if (children.Length == 1) { Walk(children[0], path, depth + 1); return; }
                    if (multi == 1)
                    {
                        Walk(children[0], path, depth + 1);
                        foreach (var child in children.Skip(1)) Walk(child, new List<Transform>(), depth + 1);
                    }
                    else
                    {
                        if (multi == 2)
                        {
                            var offset = children.Aggregate(Vector3.zero, (sum, t) => sum + t.localPosition) / children.Length;
                            if (offset.sqrMagnitude > 1e-12f) path.Add(Tip(node, offset));
                            else warnings?.Add(component.name + ": 分岐の平均位置がゼロのため分岐元は固定しました。");
                        }
                        Add(path);
                        foreach (var child in children) Walk(child, new List<Transform>(), depth + 1);
                    }
                }
                if (ignored.Contains(root)) { Skip(result, warnings, component, "Root Transformが除外対象"); continue; }
                Walk(root, new List<Transform>(), 0);
                chains.RemoveAll(chain =>
                {
                    if (!chain.Take(chain.Count - 1).Any(existing.Contains)) return false;
                    warnings?.Add(component.name + ": 既存VRM揺れ設定と重なる連鎖を省略しました。既存設定は保持します。");
                    return true;
                });
                if (chains.Count == 0) { Skip(result, warnings, component, "有効な連鎖なし（末端位置・除外・既存設定を確認）"); continue; }
                var moving = chains.SelectMany(c => c.Take(c.Count - 1)).ToArray();
                if (moving.Distinct().Count() != moving.Length || moving.Any(occupied.Contains))
                    throw Unsupported(component, "複数PhysBoneが同じボーンを駆動しています。対象の重複を解消してください");
                var group = ConvertColliders(component, copy, colliders, instance, warnings);
                foreach (var chain in chains)
                {
                    var spring = new Vrm10InstanceSpringBone.Spring(component.name);
                    // A head center would cancel the desired hair inertia.
                    if (group != null) spring.ColliderGroups.Add(group);
                    for (var i = 0; i < chain.Count; i++)
                    {
                        var node = chain[i];
                        var joint = node.GetComponent<VRM10SpringBoneJoint>() ?? node.gameObject.AddComponent<VRM10SpringBoneJoint>();
                        if (i < chain.Count - 1)
                        {
                            if (Vector3.Distance(node.position, chain[i + 1].position) < 1e-7f)
                                throw Unsupported(component, "長さゼロのボーン");
                            var t = maximumDepth > 0 ? (float)depths[node] / maximumDepth : 0;
                            ApplyForces(component, joint, t);
                            occupied.Add(node); result.Joints++;
                        }
                        spring.Joints.Add(joint);
                    }
                    instance.SpringBone.Springs.Add(spring); result.Chains++;
                }
                result.Converted++;
                ReportApproximations(component, warnings);
            }
            result.Colliders = colliders.Count;
            warnings?.Add($"PhysBone変換: 元{result.Sources}件 / 変換{result.Converted}件 / 連鎖{result.Chains}件 / 関節{result.Joints}件 / コライダー{result.Colliders}件 / 省略{result.Skipped}件。");
            if (result.Converted > 0 && (result.Chains == 0 || result.Joints == 0))
                throw new InvalidOperationException("PhysBone変換対象から揺れ設定を作成できませんでした。");
            return result;
        }

        static void ApplyForces(Component source, VRM10SpringBoneJoint joint, float depth)
        {
            // A documented approximation, not SDK solver equivalence.
            var pull = Mathf.Clamp01(Curve(source, "pull", depth, .2f));
            var spring = Mathf.Clamp01(Curve(source, "spring", depth, .2f));
            var gravity = Mathf.Clamp(Curve(source, "gravity", depth, 0), -1, 1);
            var falloff = Mathf.Clamp01(Curve(source, "gravityFalloff", depth, 0));
            joint.m_stiffnessForce = 4f * pull;
            joint.m_dragForce = .6f * (1f - spring);
            joint.m_gravityPower = Mathf.Abs(gravity) * (1f - falloff) * (EnumNumber(source, "version", 1) >= 1 ? 4f * pull : 1f);
            joint.m_gravityDir = gravity < 0 ? Vector3.up : Vector3.down;
            joint.m_jointRadius = Mathf.Max(0, Curve(source, "radius", depth, 0));
            var limit = EnumNumber(source, "limitType", 0);
            if (limit < 0 || limit > 3) throw Unsupported(source, "Limit Type");
            joint.m_anglelimitType = limit == 0 ? AnglelimitTypes.None : limit == 1 ? AnglelimitTypes.Cone :
                limit == 2 ? AnglelimitTypes.Hinge : AnglelimitTypes.Spherical;
            var rotation = Vector(source, "limitRotation", Vector3.zero);
            rotation.x *= CurveMultiplier(source, "limitRotationXCurve", depth);
            rotation.y *= CurveMultiplier(source, "limitRotationYCurve", depth);
            rotation.z *= CurveMultiplier(source, "limitRotationZCurve", depth);
            joint.m_limitSpaceOffset = Quaternion.Euler(rotation);
            joint.m_pitch = Mathf.Clamp(Curve(source, "maxAngleX", depth, 45), 0, 180) * Mathf.Deg2Rad;
            joint.m_yaw = Mathf.Clamp(Curve(source, "maxAngleZ", depth, 45), 0, 90) * Mathf.Deg2Rad;
        }

        static VRM10SpringBoneColliderGroup ConvertColliders(Component source, GameObject copy,
            Dictionary<Component, VRM10SpringBoneCollider> cache, Vrm10Instance instance, ICollection<string> warnings)
        {
            var list = new List<VRM10SpringBoneCollider>();
            foreach (var item in Items(Read<object>(source, "colliders", null)))
            {
                if (!(item is Component collider) || collider == null) continue;
                if (!Active(collider)) { warnings?.Add(source.name + ": 無効なPhysBoneコライダーを省略しました。"); continue; }
                if (collider.GetType().FullName != ColliderType) throw Unsupported(source, "未対応のコライダー型");
                var colliderRoot = Root(collider); RequireLocal(copy, colliderRoot);
                if (!colliderRoot.gameObject.activeInHierarchy)
                { warnings?.Add(source.name + ": 非表示のRoot Transformを持つコライダーを省略しました。"); continue; }
                if (!cache.TryGetValue(collider, out var converted))
                {
                    var root = colliderRoot;
                    var shape = EnumNumber(collider, "shapeType", 0);
                    var inside = Read(collider, "insideBounds", false);
                    if (shape < 0 || shape > 2 || shape == 2 && inside) throw Unsupported(collider, "Collider Shape");
                    converted = root.gameObject.AddComponent<VRM10SpringBoneCollider>();
                    converted.ColliderType = shape == 0 ? (inside ? VRM10SpringBoneColliderTypes.SphereInside : VRM10SpringBoneColliderTypes.Sphere) :
                        shape == 1 ? (inside ? VRM10SpringBoneColliderTypes.CapsuleInside : VRM10SpringBoneColliderTypes.Capsule) : VRM10SpringBoneColliderTypes.Plane;
                    var rotation = Read(collider, "rotation", Quaternion.identity);
                    if (!Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w)) throw Unsupported(collider, "非有限の回転");
                    converted.Offset = Vector(collider, "position", Vector3.zero);
                    converted.Radius = Mathf.Max(0, Number(collider, "radius", 0));
                    if (shape == 1)
                    {
                        var offset = rotation * Vector3.up * Mathf.Max(0, Number(collider, "height", 0) * .5f - converted.Radius);
                        converted.Tail = converted.Offset + offset; converted.Offset -= offset;
                    }
                    if (shape == 2) converted.Normal = rotation * Vector3.up;
                    cache.Add(collider, converted);
                }
                if (!list.Contains(converted)) list.Add(converted);
            }
            if (list.Count == 0) return null;
            var group = source.gameObject.AddComponent<VRM10SpringBoneColliderGroup>();
            group.Colliders.AddRange(list); instance.SpringBone.ColliderGroups.Add(group);
            return group;
        }

        static void ReportApproximations(Component source, ICollection<string> warnings)
        {
            warnings?.Add(source.name + ": 揺れの力・減衰・重力はVRM向けの近似です。VRChatと同一の物理演算ではありません。");
            if (EnumNumber(source, "integrationType", 0) != 0 || Number(source, "stiffness", 0) != 0)
                warnings?.Add(source.name + ": Advanced積分／Stiffnessの独立効果は再現せず、PullとSpringから近似しました。");
            if (Number(source, "immobile", 0) != 0 || Number(source, "gravityFalloff", 0) != 0)
                warnings?.Add(source.name + ": Immobileはアプリの安定化に委ね、Gravity Falloffは重力の減衰として近似しました。");
            if (Number(source, "maxStretch", 0) != 0 || Number(source, "maxSquish", 0) != 0 || Number(source, "stretchMotion", 0) != 0)
                warnings?.Add(source.name + ": 伸縮は保存せず、元のボーン長を保持しました。");
            if (Read(source, "isAnimated", false)) warnings?.Add(source.name + ": Is Animatedは出力時の姿勢を基準とします。");
            if (EnumNumber(source, "limitType", 0) != 0)
                warnings?.Add(source.name + ": 角度制限はUniVRMの制限へ近似しました。PolarのZ角度は0〜90度に制限します。");
            warnings?.Add(source.name + ": Grab・Pose・外部衝突・PhysBoneパラメーターの連動は変換対象外です。");
        }

        internal static void VerifyOutput(byte[] bytes, Result result)
        {
            if (result == null || result.Converted == 0) return;
            var document = GlbDocument.Read(bytes);
            var extensions = document.Json.TryGetValue("extensions", out var raw) ? raw as Dictionary<string, object> : null;
            var springs = extensions != null && extensions.TryGetValue("VRMC_springBone", out raw) ? raw as Dictionary<string, object> : null;
            var chains = springs != null && springs.TryGetValue("springs", out raw) ? raw as List<object> : null;
            var movingJoints = chains?.OfType<Dictionary<string, object>>().Sum(chain =>
                chain.TryGetValue("joints", out var joints) && joints is List<object> list ? Math.Max(0, list.Count - 1) : 0) ?? 0;
            var colliderCount = springs != null && springs.TryGetValue("colliders", out raw) && raw is List<object> colliderList ? colliderList.Count : 0;
            if (chains == null || chains.Count < result.ExistingChains + result.Chains ||
                movingJoints < result.ExistingJoints + result.Joints || colliderCount < result.ExistingColliders + result.Colliders)
                throw new InvalidOperationException("出力VRMから変換済みの揺れ設定が失われました。ファイルは保存しません。");
        }

        static Transform Root(Component c)
        {
            var root = Read<Transform>(c, "rootTransform", null);
            // Unity's missing-object sentinel is not CLR null.
            return root != null ? root : c.transform;
        }
        static void RequireLocal(GameObject copy, Transform node)
        {
            if (node == null || node != copy.transform && !node.IsChildOf(copy.transform))
                throw new InvalidOperationException("PhysBoneの参照先がアバターの外にあります。");
        }
        static void Skip(Result result, ICollection<string> warnings, Component component, string reason)
        { result.Skipped++; warnings?.Add(component.name + ": PhysBoneを省略 — " + reason); }
        static Exception Unsupported(Component component, string reason) => new InvalidOperationException(component.name + ": PhysBoneを変換できません: " + reason);
        static IEnumerable<object> Items(object value) => value is IEnumerable items ? items.Cast<object>() : Enumerable.Empty<object>();
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static float Number(Component source, string name, float fallback)
        { var value = Read(source, name, fallback); if (!Finite(value)) throw Unsupported(source, name + "が非有限値"); return value; }
        static Vector3 Vector(Component source, string name, Vector3 fallback)
        { var v = Read(source, name, fallback); if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z)) throw Unsupported(source, name + "が非有限値"); return v; }
        static float Curve(Component source, string name, float depth, float fallback)
        { var value = Number(source, name, fallback) * CurveMultiplier(source, name + "Curve", depth); if (!Finite(value)) throw Unsupported(source, name + "Curveが非有限値"); return value; }
        static float CurveMultiplier(Component source, string name, float depth)
        { var curve = Read<AnimationCurve>(source, name, null); var value = curve == null || curve.length == 0 ? 1 : curve.Evaluate(depth); if (!Finite(value)) throw Unsupported(source, name + "が非有限値"); return value; }
        static int EnumNumber(Component source, string name, int fallback) => System.Convert.ToInt32(Member(source, name) ?? fallback);
        static T Read<T>(Component source, string name, T fallback) => Member(source, name) is T value ? value : fallback;
        static object Member(Component source, string name)
        {
            for (var type = source.GetType(); type != null; type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var field = type.GetField(name, flags); if (field != null) return field.GetValue(source);
                var property = type.GetProperty(name, flags); if (property != null) return property.GetValue(source);
            }
            return null;
        }
    }
}
