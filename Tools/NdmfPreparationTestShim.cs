#if NDMF_PREPARATION_BEHAVIOR_TESTS
// Host protocol/asset-graph adapter only. Does not emulate Unity serialization,
// native mesh deformation, or MA/NDMF execution. Installed-package tests skip.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityEngine
{
    public static class Debug { public static void LogWarning(object message) { } }
    public class Object
    {
        public string name; internal bool destroyed, persistent;
        static bool Null(Object value) => ReferenceEquals(value, null) || value.destroyed;
        public static bool operator ==(Object a, Object b) => Null(a) ? Null(b) : !Null(b) && ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object value) => ReferenceEquals(this, value);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        public static T Instantiate<T>(T value) where T : Object
        {
            if (value is GameObject) throw new NotSupportedException("Unity hierarchy cloning requires installed-package tests.");
            var copy = (T)value.MemberwiseClone(); copy.persistent = false; copy.destroyed = false; return copy;
        }
        public static void DestroyImmediate(Object value)
        {
            if (value == null) return;
            if (value.persistent) throw new InvalidOperationException("Cannot destroy a persistent asset.");
            value.destroyed = true;
            if (value is GameObject root)
                foreach (var component in root.GetComponentsInChildren<Component>(true)) component.destroyed = true;
        }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }
    public class MonoBehaviour : Component { }
    public class ScriptableObject : Object { public static T CreateInstance<T>() where T : ScriptableObject, new() => new T(); }
    public sealed class GameObject : Object
    {
        public readonly List<Component> components = new List<Component>();
        public readonly Transform transform;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public void SetActive(bool value) { activeSelf = value; }
        public GameObject(string name) { this.name = name; transform = new Transform { gameObject = this }; components.Add(transform); }
        public T AddComponent<T>() where T : Component, new() { var value = new T { gameObject = this }; components.Add(value); return value; }
        public Component AddComponent(Type type) { var value = (Component)Activator.CreateInstance(type); value.gameObject = this; components.Add(value); return value; }
        public T GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault(x => x != null);
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component => components.OfType<T>()
            .Concat(transform.children.SelectMany(c => c.gameObject.GetComponentsInChildren<T>(includeInactive))).Where(x => x != null).ToArray();
    }
    public sealed class Transform : Component
    {
        public Transform parent; public readonly List<Transform> children = new List<Transform>();
        public Vector3 localPosition; public Quaternion localRotation = Quaternion.identity;
        public Matrix4x4 worldToLocalMatrix => default; public Matrix4x4 localToWorldMatrix => default;
        public void SetParent(Transform target, bool worldPositionStays = true) { parent?.children.Remove(this); parent = target; target?.children.Add(this); }
        public bool IsChildOf(Transform root) { for (var cursor = this; cursor != null; cursor = cursor.parent) if (cursor == root) return true; return false; }
        public Transform Find(string path) { var cursor = this; foreach (var part in path.Split('/')) cursor = cursor.children.FirstOrDefault(c => c.gameObject.name == part); return cursor; }
    }
    public struct Vector3
    {
        public float x,y,z; public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public static Vector3 zero => new Vector3(); public static Vector3 right => new Vector3(1,0,0); public static Vector3 up => new Vector3(0,1,0); public static Vector3 down => new Vector3(0,-1,0);
        public static float Distance(Vector3 a, Vector3 b) => (float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z));
    }
    public struct Quaternion { public static Quaternion identity => default; public static Quaternion Euler(float x,float y,float z) => default; }
    public struct Matrix4x4 { public static Matrix4x4 operator *(Matrix4x4 a,Matrix4x4 b) => default; }
    public struct BoneWeight { public int boneIndex0; public float weight0; }
    public struct Color { private int value; public static Color red => new Color { value = 1 }; public static Color blue => new Color { value = 2 }; }
    public class Shader : Object { public static Shader Find(string name) => new Shader { name = name, persistent = true }; }
    public class ComputeShader : Object { }
    public class Texture : Object { }
    public class Material : Object { public Shader shader; public Color color; public Material(Shader shader) { this.shader = shader; } }
    public class AnimationClip : Object { public float frameRate; }
    public class Mesh : Object
    {
        public Vector3[] vertices; public int[] triangles; public Matrix4x4[] bindposes; public BoneWeight[] boneWeights;
        private readonly List<string> names = new List<string>();
        public int blendShapeCount => names.Count;
        public string GetBlendShapeName(int index) => names[index];
        public int GetBlendShapeIndex(string name) => names.IndexOf(name);
        public void AddBlendShapeFrame(string name,float weight,Vector3[] vertices,Vector3[] normals,Vector3[] tangents) => names.Add(name);
    }
    public class Renderer : Component { public bool enabled = true; }
    public class SkinnedMeshRenderer : Renderer
    {
        public Mesh sharedMesh; public Transform[] bones; public Transform rootBone;
        public void BakeMesh(Mesh target) => throw new NotSupportedException("Native skinning requires Unity.");
    }
    public enum HumanBodyBones { Hips, Head, LastBone }
    public class Avatar : Object { public bool isValid, isHuman; }
    public class Animator : Component
    {
        public Avatar avatar;
        public readonly Dictionary<HumanBodyBones,Transform> bones = new Dictionary<HumanBodyBones,Transform>();
        public Transform GetBoneTransform(HumanBodyBones bone) => bones.TryGetValue(bone, out var value) ? value : null;
    }
}
namespace UnityEditor
{
    using Object = UnityEngine.Object;
    public class MonoScript : Object { }
    public static class EditorUtility
    {
        public static bool IsPersistent(Object value) => value != null && value.persistent;
        public static Object[] CollectDependencies(Object[] roots)
        {
            var result = new HashSet<Object>();
            void Visit(object raw)
            {
                if (raw is Object value)
                {
                    if (value == null || !result.Add(value)) return;
                    foreach (var field in Fields(value.GetType())) Visit(field.GetValue(value));
                }
                else if (raw is IEnumerable values && !(raw is string)) foreach (var item in values) Visit(item);
            }
            foreach (var root in roots) Visit(root);
            return result.ToArray();
        }
        internal static IEnumerable<FieldInfo> Fields(Type type)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) yield return field;
        }
    }
    public enum SerializedPropertyType { ObjectReference }
    public sealed class SerializedObject : IDisposable
    {
        readonly Object target;
        public SerializedObject(Object target) { this.target = target; }
        public SerializedProperty GetIterator() => new SerializedProperty(target);
        public bool ApplyModifiedPropertiesWithoutUndo() => true;
        public void Dispose() { }
    }
    public sealed class SerializedProperty
    {
        readonly Object target; readonly FieldInfo[] fields; int position = -1;
        public SerializedProperty(Object target) { this.target = target; fields = EditorUtility.Fields(target.GetType()).Where(f => typeof(Object).IsAssignableFrom(f.FieldType)).ToArray(); }
        public bool Next(bool children) => ++position < fields.Length;
        public SerializedPropertyType propertyType => SerializedPropertyType.ObjectReference;
        public Object objectReferenceValue { get => (Object)fields[position].GetValue(target); set => fields[position].SetValue(target, value); }
    }
    public static class AssetDatabase
    {
        static readonly Dictionary<string,Object> assets = new Dictionary<string,Object>();
        static readonly Dictionary<string,string> folders = new Dictionary<string,string>();
        public static string CreateFolder(string parent,string name) { var path=parent+"/"+name; var guid=Guid.NewGuid().ToString("N"); folders.Add(path,guid); return guid; }
        public static string GUIDToAssetPath(string guid) => folders.FirstOrDefault(item => item.Value==guid).Key ?? "";
        public static string AssetPathToGUID(string path) => folders.TryGetValue(path,out var guid) ? guid : "";
        public static bool IsValidFolder(string path) => path=="Assets" || folders.ContainsKey(path);
        public static void CreateAsset(Object value,string path) { value.persistent = true; assets.Add(path,value); }
        public static bool DeleteAsset(string path)
        {
            if (folders.Remove(path))
            {
                foreach (var child in assets.Keys.Where(key => key.StartsWith(path+"/",StringComparison.Ordinal)).ToArray()) DeleteAsset(child);
                return true;
            }
            if (!assets.TryGetValue(path,out var value)) return false;
            assets.Remove(path); value.persistent = false; Object.DestroyImmediate(value); return true;
        }
    }
}
namespace UnityEditor.PackageManager
{
    public class PackageInfo { public string version; public static PackageInfo FindForAssembly(Assembly assembly) => null; }
}
// Authoring metadata adapter only; the real pipeline tests still require the
// installed NDMFAvatarRoot and canonical NDMF classes and remain skipped here.
namespace nadena.dev.modular_avatar.core
{
    public class AvatarTagComponent : UnityEngine.MonoBehaviour { }
    public sealed class AvatarObjectReference
    {
        public UnityEngine.GameObject target;
        public void Set(UnityEngine.GameObject value) { target = value; }
    }
    public sealed class ModularAvatarMergeArmature : AvatarTagComponent
    {
        public AvatarObjectReference mergeTarget = new AvatarObjectReference();
        public UnityEngine.GameObject mergeTargetObject => mergeTarget.target;
    }
}
namespace NUnit.Framework
{
    public class TestAttribute : Attribute { }
    public class SetUpAttribute : Attribute { }
    public class TearDownAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class TestCaseAttribute : Attribute { public readonly object[] Values; public TestCaseAttribute(params object[] values) { Values = values ?? new object[] { null }; } }
    public class IgnoredException : Exception { }
    public static class Assert
    {
        public static int Assertions;
        public static void IsTrue(bool value,string message = null) { Assertions++; if (!value) throw new Exception(message ?? "Expected true"); }
        public static void IsFalse(bool value,string message = null) => IsTrue(!value,message);
        public static void IsNull(object value) => IsTrue(value == null);
        public static void IsNotNull(object value) => IsTrue(value != null);
        public static void AreSame(object a,object b,string message = null) => IsTrue(ReferenceEquals(a,b),message);
        public static void AreNotSame(object a,object b,string message = null) => IsTrue(!ReferenceEquals(a,b),message);
        public static void AreEqual(object a,object b,string message = null) => IsTrue(Equals(a,b),message ?? $"Expected {a}; got {b}");
        public static void Greater(float a,float b,string message = null) => IsTrue(a>b,message);
        public static void GreaterOrEqual(int a,int b) => IsTrue(a>=b);
        public static T Throws<T>(Action action) where T : Exception { Assertions++; try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
        public static void Ignore(string reason) => throw new IgnoredException();
    }
    public static class StringAssert { public static void Contains(string part,string value) => Assert.IsTrue(value.Contains(part)); }
    public static class CollectionAssert { public static void AreEqual(IEnumerable a,IEnumerable b) => Assert.IsTrue(a.Cast<object>().SequenceEqual(b.Cast<object>())); }
}
public static class NdmfPreparationHostTests
{
    public static void Run()
    {
        var type = typeof(VRVlog.LilToonExporter.Tests.NdmfPreparationTests); int passed = 0, skipped = 0;
        foreach (var method in type.GetMethods())
        {
            var cases = method.GetCustomAttributes<NUnit.Framework.TestCaseAttribute>().Select(c => c.Values).ToList();
            if (method.GetCustomAttribute<NUnit.Framework.TestAttribute>() != null) cases.Add(Array.Empty<object>());
            foreach (var args in cases)
            {
                var test = new VRVlog.LilToonExporter.Tests.NdmfPreparationTests(); test.SetUp();
                try { method.Invoke(test,args); passed++; }
                catch (TargetInvocationException error) when (error.InnerException is NUnit.Framework.IgnoredException) { skipped++; }
                catch (TargetInvocationException error) { throw new Exception(method.Name + " failed",error.InnerException); }
                finally { test.TearDown(); }
            }
        }
        Console.WriteLine($"NDMF preparation host checks passed: {passed} cases / {NUnit.Framework.Assert.Assertions} assertions; {skipped} installed-package Unity tests skipped. Asset graph adapter, not Unity/MA validation.");
    }
}
#endif
