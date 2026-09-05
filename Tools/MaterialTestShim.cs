#if EXPORTER_BEHAVIOR_TESTS
// Property-bag test double only. This lets CI execute the production material
// reader without Unity; it does not validate Unity shader execution or imports.
namespace UnityEngine
{
    public class Object
    {
        public static void DestroyImmediate(Object value) => throw new System.NotSupportedException("Unity lifetime is not simulated.");
    }
    public class Texture : Object { public string name; }
    public class Texture2D : Texture
    {
        public static readonly Texture2D whiteTexture = new Texture2D();
        public int width, height, mipmapCount;
        public FilterMode filterMode;
        public TextureWrapMode wrapModeU, wrapModeV;
        public Texture2D() { }
        public Texture2D(int w, int h, TextureFormat format, bool mip, bool linear) => throw new System.NotSupportedException();
        public void ReadPixels(Rect rect, int x, int y, bool mip) => throw new System.NotSupportedException();
        public void Apply(bool mip, bool unreadable) => throw new System.NotSupportedException();
    }
    // Flat renderer inventory; hierarchy behavior is covered by Unity tests.
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public readonly System.Collections.Generic.List<Renderer> Renderers = new System.Collections.Generic.List<Renderer>();
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<T>(Renderers));
    }
    public class Renderer
    {
        public GameObject gameObject = new GameObject();
        public bool enabled = true;
        public Material[] sharedMaterials = System.Array.Empty<Material>();
    }
    public enum FilterMode { Point, Bilinear, Trilinear }
    public enum TextureWrapMode { Clamp, Mirror, Repeat }
    public enum TextureFormat { RGBA32 }
    public enum RenderTextureFormat { ARGB32 }
    public enum RenderTextureReadWrite { sRGB }
    public struct Rect { public Rect(float x, float y, float w, float h) { } }
    public class RenderTexture : Texture
    {
        public static RenderTexture active;
        public static RenderTexture GetTemporary(int w, int h, int d, RenderTextureFormat f, RenderTextureReadWrite r) => throw new System.NotSupportedException("GPU operations must not run in these tests.");
        public static void ReleaseTemporary(RenderTexture value) => throw new System.NotSupportedException();
    }
    public static class Graphics { public static void Blit(Texture a, RenderTexture b) => throw new System.NotSupportedException(); }
    public static class ImageConversion { public static byte[] EncodeToPNG(Texture2D value) => throw new System.NotSupportedException(); }
    public class Shader { public string name; }
    public struct Vector2 { public float x, y; }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color black => new Color(0, 0, 0);
    }
    public static class Mathf { public static int RoundToInt(float value) => (int)System.Math.Round(value); }
    public class Material
    {
        public string name;
        public Shader shader = new Shader { name = "lilToon" };
        public int renderQueue = 2000;
        public readonly System.Collections.Generic.Dictionary<string, object> Properties = new System.Collections.Generic.Dictionary<string, object>();
        public bool HasProperty(string name) => Properties.ContainsKey(name);
        public float GetFloat(string name) => (float)Properties[name];
        public Color GetColor(string name) => (Color)Properties[name];
        public Texture GetTexture(string name) => Properties[name] as Texture;
        public Vector2 GetTextureScale(string name) => new Vector2 { x = 1, y = 1 };
        public Vector2 GetTextureOffset(string name) => new Vector2();
    }
}
#endif
