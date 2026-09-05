#if EXPORTER_BEHAVIOR_TESTS
// Property-bag test double only. This lets CI execute the production material
// reader without Unity; it does not validate Unity shader execution or imports.
namespace UnityEngine
{
    public class Texture { public string name; }
    public class Texture2D : Texture { public static readonly Texture2D whiteTexture = new Texture2D(); }
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
