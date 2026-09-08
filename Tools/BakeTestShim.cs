#if EXPORTER_BAKE_TESTS
// Property/hierarchy test doubles only. This runner executes production policy,
// never claims Unity or shader compatibility, and fails on every GPU operation.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityEngine
{
    public class Object
    {
        public static int Created;
        public string name = "";
        public Object() { Created++; }
        public static void DestroyImmediate(Object value) { }
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
    }

    public sealed class Transform : Component
    {
        public Transform parent;
        public bool IsChildOf(Transform ancestor)
        {
            for (var current = this; current != null; current = current.parent)
                if (current == ancestor) return true;
            return false;
        }
    }

    public sealed class GameObject : Object
    {
        public static int Count;
        public readonly Transform transform;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        private readonly List<Component> components = new List<Component>();
        private readonly List<GameObject> children = new List<GameObject>();
        public GameObject(string name)
        {
            Count++;
            this.name = name;
            transform = new Transform { gameObject = this };
        }
        public GameObject Child(string name)
        {
            var result = new GameObject(name);
            result.transform.parent = transform;
            children.Add(result);
            return result;
        }
        public T AddComponent<T>() where T : Component, new()
        {
            var result = new T { gameObject = this };
            components.Add(result);
            return result;
        }
        public T GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault();
        public T[] GetComponentsInChildren<T>() where T : Component
        {
            if (!activeInHierarchy) return Array.Empty<T>();
            return components.OfType<T>().Concat(children.SelectMany(child => child.GetComponentsInChildren<T>())).ToArray();
        }
    }

    public class Renderer : Component
    {
        public bool enabled = true;
        private Material[] slots = Array.Empty<Material>();
        public Material[] sharedMaterials { get => (Material[])slots.Clone(); set => slots = (Material[])value.Clone(); }
    }
    public sealed class MeshRenderer : Renderer { }
    public sealed class SkinnedMeshRenderer : Renderer { public Mesh sharedMesh; }
    public sealed class MeshFilter : Component { public Mesh sharedMesh; }
    public sealed class Mesh : Object
    {
        public Vector2[] uv = Array.Empty<Vector2>();
        public int[][] indices = Array.Empty<int[]>();
        public bool unreadable;
        public int subMeshCount => indices.Length;
        public int[] GetIndices(int subMesh)
        {
            if (unreadable) throw new UnityException("The mesh is not readable.");
            return (int[])indices[subMesh].Clone();
        }
    }

    public sealed class Shader : Object
    {
        public bool isSupported = true;
        public static Shader Find(string name) => new Shader { name = name };
    }

    public sealed class Material : Object
    {
        public static int Count;
        public Shader shader;
        private Dictionary<string, object> properties = new Dictionary<string, object>();
        private Dictionary<string, Vector2> scales = new Dictionary<string, Vector2>();
        private Dictionary<string, Vector2> offsets = new Dictionary<string, Vector2>();
        private Dictionary<string, string> tags = new Dictionary<string, string>();
        private HashSet<string> keywords = new HashSet<string>();
        public string[] shaderKeywords { get => keywords.ToArray(); set => keywords = new HashSet<string>(value); }
        public Material(Shader shader)
        {
            Count++;
            this.shader = shader;
            tags["RenderType"] = "Opaque";
            SetVector("_MainTexHSVG", new Vector4(0, 1, 1, 1));
            SetColor("_Color", Color.white);
            // Relevant defaults from official lilToon 2.3.4 lts.shader.
            foreach (var layer in new[] { "2nd", "3rd" })
            {
                var p = "_Main" + layer + "Tex";
                SetFloat("_UseMain" + layer + "Tex", 0);
                SetColor("_Color" + layer, Color.white);
                SetVector(p + "DecalAnimation", new Vector4(1, 1, 1, 30));
                SetVector(p + "DecalSubParam", new Vector4(1, 1, 0, 1));
                SetFloat("_Main" + layer + "EnableLighting", 1);
                SetVector("_Main" + layer + "DissolveParams", new Vector4(0, 0, .5f, .1f));
                SetVector("_Main" + layer + "DistanceFade", new Vector4(.1f, .01f, 0, 0));
            }
        }
        public Material(Material source)
        {
            Count++;
            shader = source.shader;
            name = source.name;
            CopyPropertiesFromMaterial(source);
        }
        public void CopyPropertiesFromMaterial(Material source)
        {
            properties = new Dictionary<string, object>(source.properties);
            scales = new Dictionary<string, Vector2>(source.scales);
            offsets = new Dictionary<string, Vector2>(source.offsets);
            tags = new Dictionary<string, string>(source.tags);
            keywords = new HashSet<string>(source.keywords);
        }
        public bool HasProperty(string key) => properties.ContainsKey(key);
        public void SetFloat(string key, float value) => properties[key] = value;
        public float GetFloat(string key) => properties.TryGetValue(key, out var value) ? (float)value : 0;
        public void SetVector(string key, Vector4 value) => properties[key] = value;
        public Vector4 GetVector(string key) => properties.TryGetValue(key, out var value) ? (Vector4)value : Vector4.zero;
        public void SetColor(string key, Color value) => properties[key] = value;
        public Color GetColor(string key) => properties.TryGetValue(key, out var value) ? (Color)value : Color.black;
        public void SetTexture(string key, Texture value) => properties[key] = value;
        public Texture GetTexture(string key) => properties.TryGetValue(key, out var value) ? (Texture)value : null;
        public void SetTextureScale(string key, Vector2 value) => scales[key] = value;
        public Vector2 GetTextureScale(string key) => scales.TryGetValue(key, out var value) ? value : Vector2.one;
        public void SetTextureOffset(string key, Vector2 value) => offsets[key] = value;
        public Vector2 GetTextureOffset(string key) => offsets.TryGetValue(key, out var value) ? value : Vector2.zero;
        public string GetTag(string key, bool searchFallbacks, string defaultValue) => tags.TryGetValue(key, out var value) ? value : defaultValue;
        public void SetOverrideTag(string key, string value) => tags[key] = value;
        public bool IsKeywordEnabled(string keyword) => keywords.Contains(keyword);
        public void EnableKeyword(string keyword) => keywords.Add(keyword);
        public string Snapshot() => string.Join("|", properties.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) +
            string.Join("|", scales.OrderBy(p => p.Key)) + string.Join("|", offsets.OrderBy(p => p.Key)) +
            string.Join("|", tags.OrderBy(p => p.Key)) + string.Join("|", keywords.OrderBy(k => k));
    }

    public struct Vector2 : IEquatable<Vector2>
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public bool Equals(Vector2 value) => x == value.x && y == value.y;
        public override bool Equals(object value) => value is Vector2 vector && Equals(vector);
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode();
        public static bool operator ==(Vector2 a, Vector2 b) => a.Equals(b);
        public static bool operator !=(Vector2 a, Vector2 b) => !a.Equals(b);
        public override string ToString() => FormattableString.Invariant($"({x}, {y})");
    }
    public struct Vector4 : IEquatable<Vector4>
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vector4 zero => new Vector4(0, 0, 0, 0);
        public bool Equals(Vector4 value) => x == value.x && y == value.y && z == value.z && w == value.w;
        public override bool Equals(object value) => value is Vector4 vector && Equals(vector);
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode() ^ w.GetHashCode();
        public static bool operator ==(Vector4 a, Vector4 b) => a.Equals(b);
        public static bool operator !=(Vector4 a, Vector4 b) => !a.Equals(b);
        public override string ToString() => FormattableString.Invariant($"({x}, {y}, {z}, {w})");
    }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
        public static Color black => new Color(0, 0, 0);
        public static Color red => new Color(1, 0, 0);
        public override string ToString() => FormattableString.Invariant($"({r}, {g}, {b}, {a})");
    }
    public struct Rect { public Rect(float x, float y, float width, float height) { } }
    public sealed class UnityException : Exception { public UnityException(string message) : base(message) { } }
    public static class Mathf
    {
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int RoundToInt(float value) => (int)Math.Round(value);
    }
    public enum ColorSpace { Gamma, Linear }
    public enum TextureFormat { RGBA32 }
    public enum RenderTextureFormat { ARGB32 }
    public enum RenderTextureReadWrite { sRGB }
    public enum TextureWrapMode { Repeat, Clamp }
    public enum FilterMode { Point, Bilinear }
    public class Texture : Object
    {
        public int width = 4, height = 4;
        public TextureWrapMode wrapModeU, wrapModeV;
        public FilterMode filterMode;
    }
    public sealed class Texture2D : Texture
    {
        public static int Count;
        public static readonly Texture2D whiteTexture = Fixture("white");
        private Texture2D() { Count++; }
        public static Texture2D Fixture(string name) => new Texture2D { name = name };
        public Texture2D(int width, int height, TextureFormat format, bool mipChain, bool linear) { GpuForbidden.Fail("Texture2D allocation"); }
        public void ReadPixels(Rect area, int x, int y, bool recalculateMipMaps) => GpuForbidden.Fail("ReadPixels");
        public void Apply(bool updateMipMaps, bool makeNoLongerReadable) => GpuForbidden.Fail("Apply");
    }
    public sealed class RenderTexture : Texture
    {
        public static RenderTexture active;
        public static RenderTexture GetTemporary(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
        { GpuForbidden.Fail("RenderTexture allocation"); return null; }
        public static void ReleaseTemporary(RenderTexture target) => GpuForbidden.Fail("ReleaseTemporary");
    }
    public static class Graphics
    {
        public static void Blit(Texture source, RenderTexture target, Material material, int pass) => GpuForbidden.Fail("Graphics.Blit");
    }
    public static class GL { public static bool sRGBWrite; }
    public static class QualitySettings { public static ColorSpace activeColorSpace = ColorSpace.Gamma; }
    public static class GpuForbidden
    {
        public static int Calls;
        public static void Fail(string operation)
        {
            Calls++;
            throw new InvalidOperationException("GPU operations are forbidden in host policy tests: " + operation);
        }
    }
}

namespace UnityEditor
{
    public static class AnimationUtility
    {
        public static string CalculateTransformPath(UnityEngine.Transform child, UnityEngine.Transform root)
        {
            var parts = new Stack<string>();
            for (var current = child; current != null && current != root; current = current.parent)
                parts.Push(current.gameObject.name);
            return string.Join("/", parts);
        }
    }
}

namespace VRVlog.LilToonExporter
{
    internal static class LilToonMaterialReader
    {
        internal static bool IsLilToon(UnityEngine.Material material) => material != null && material.shader != null &&
            material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    // Emission policies have their own production tests; this runner isolates
    // layer preflight and omission and never exercises suppression branches.
    internal static class LilToonEmissionPolicy
    {
        internal static bool IsSuppressed(UnityEngine.Material material, bool enabled) => false;
        internal static bool HasHdrTextureEmission(UnityEngine.Material material) => false;
    }
    internal static class LilToonMobileProfile { internal const int MaximumTextureSize = 2048; internal const int DefaultMaximumTextureSize = 1024; }
}
#endif
