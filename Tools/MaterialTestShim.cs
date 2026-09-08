#if EXPORTER_BEHAVIOR_TESTS
// Property-bag test double only. This lets CI execute the production material
// reader without Unity; it does not validate Unity shader execution or imports.
namespace UnityEngine
{
    public class Object
    {
        public static void DestroyImmediate(Object value) { if (!(value is Mesh)) throw new System.NotSupportedException("Unity lifetime is not simulated."); }
        public static T Instantiate<T>(T value) where T : Object => throw new System.NotSupportedException("Unity cloning is covered by Editor tests.");
    }
    public class Texture : Object { public string name; public int width, height; }
    public class Component : Object { }
    public class RuntimeAnimatorController : Object { }
    public class Texture2D : Texture
    {
        public static readonly Texture2D whiteTexture = new Texture2D();
        public int mipmapCount;
        public FilterMode filterMode;
        public TextureWrapMode wrapModeU, wrapModeV;
        public Texture2D() { }
        public Texture2D(int w, int h, TextureFormat format, bool mip, bool linear) => throw new System.NotSupportedException();
        public void ReadPixels(Rect rect, int x, int y, bool mip) => throw new System.NotSupportedException();
        public void Apply(bool mip, bool unreadable) => throw new System.NotSupportedException();
        public Color32[] GetPixels32() => throw new System.NotSupportedException("Image readback requires Unity.");
        public void SetPixels32(Color32[] pixels) => throw new System.NotSupportedException("Image writes require Unity.");
    }
    // Flat renderer inventory; hierarchy behavior is covered by Unity tests.
    public class GameObject
    {
        public T[] GetComponents<T>() => throw new System.NotSupportedException("Unity descriptor lookup requires Editor tests.");
        public Transform transform;
        public bool activeInHierarchy = true;
        public readonly System.Collections.Generic.List<Renderer> Renderers = new System.Collections.Generic.List<Renderer>();
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OfType<T>(Renderers));
    }
    public class Renderer
    {
        public string name;
        public Transform transform;
        public T[] GetComponents<T>() => throw new System.NotSupportedException();
        public GameObject gameObject = new GameObject();
        public bool enabled = true;
        public Material[] sharedMaterials = System.Array.Empty<Material>();
    }
    public enum FilterMode { Point, Bilinear, Trilinear }
    public enum TextureWrapMode { Clamp, Mirror, Repeat }
    public enum TextureFormat { RGBA32 }
    public enum RenderTextureFormat { ARGB32 }
    public enum RenderTextureReadWrite { sRGB, Linear }
    public enum ColorSpace { Gamma, Linear }
    public static class GL { public static bool sRGBWrite; }
    public static class QualitySettings { public static ColorSpace activeColorSpace = ColorSpace.Gamma; }
    public struct Color32 { public byte r,g,b,a; public Color32(byte r,byte g,byte b,byte a){this.r=r;this.g=g;this.b=b;this.a=a;} }
    public struct Rect { public Rect(float x, float y, float w, float h) { } }
    public class RenderTexture : Texture
    {
        public static RenderTexture active;
        public static RenderTexture GetTemporary(int w, int h, int d, RenderTextureFormat f, RenderTextureReadWrite r) => throw new System.NotSupportedException("GPU operations must not run in these tests.");
        public static void ReleaseTemporary(RenderTexture value) => throw new System.NotSupportedException();
    }
    public static class Graphics { public static void Blit(Texture a, RenderTexture b) => throw new System.NotSupportedException(); }
    public static class ImageConversion
    {
        public static byte[] EncodeToPNG(Texture2D value) => throw new System.NotSupportedException();
        public static bool LoadImage(Texture2D value, byte[] bytes, bool markNonReadable) => throw new System.NotSupportedException("Image codecs require Unity.");
    }
    public class Shader { public string name; }
    public struct Vector2 { public float x, y; }

    public struct Color
    {
        public float r,g,b,a;
        public Color(float r,float g,float b,float a=1) { this.r=r;this.g=g;this.b=b;this.a=a; }
        static float ToLinear(float v) => v <= .04045f ? v / 12.92f : (float)System.Math.Pow((v+.055f)/1.055f,2.4);
        static float ToGamma(float v) => v <= .0031308f ? v * 12.92f : 1.055f*(float)System.Math.Pow(v,1/2.4)-.055f;
        public Color linear => new Color(ToLinear(r),ToLinear(g),ToLinear(b),a);
        public Color gamma => new Color(ToGamma(r),ToGamma(g),ToGamma(b),a);
        public static Color black => new Color(0,0,0);
    }
    public static class Mathf { public static float Clamp01(float v) => System.Math.Clamp(v,0,1); public static int RoundToInt(float value) => (int)System.Math.Round(value); }
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
namespace UniGLTF
{
    public enum ColorSpace { sRGB, Linear }
    public interface ITextureSerializer
    {
        bool CanExportAsEditorAssetFile(UnityEngine.Texture texture, ColorSpace exportColorSpace);
        (byte[] bytes, string mime) ExportBytesWithMime(UnityEngine.Texture2D texture, ColorSpace exportColorSpace);
        void ModifyTextureAssetBeforeExporting(UnityEngine.Texture texture);
    }
    public static class TextureConverter
    {
        public static UnityEngine.Texture2D CopyTexture(UnityEngine.Texture source, ColorSpace colorSpace, bool alpha, UnityEngine.Material material) =>
            throw new System.NotSupportedException("Texture conversion requires Unity and is not simulated.");
    }
}
#endif
