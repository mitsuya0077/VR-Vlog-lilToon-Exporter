using System;
using System.Collections.Generic;

namespace VRVlog.LilToonExporter
{
    [Serializable]
    public sealed class LilToonExtensionRoot
    {
        public int schemaMajor = LilToonMobileProfile.SchemaMajor;
        public int schemaMinor;
        public string exporterVersion = "";
        public string sourceLilToonVersion = "";
        public List<LilToonMaterialRecord> materials = new List<LilToonMaterialRecord>();
    }

    [Serializable]
    public sealed class LilToonMaterialRecord
    {
        public int materialIndex = -1;
        public string shaderFamily = "";
        public string renderMode = "opaque";
        public int renderQueue = -1;
        public string cullMode = "back";
        public List<string> features = new List<string>();
        public List<LilToonFloatProperty> floats = new List<LilToonFloatProperty>();
        public List<LilToonColorProperty> colors = new List<LilToonColorProperty>();
        public List<LilToonTextureProperty> textures = new List<LilToonTextureProperty>();
    }

    [Serializable]
    public sealed class LilToonFloatProperty
    {
        public string name = "";
        public float value;
    }

    [Serializable]
    public sealed class LilToonColorProperty
    {
        public string name = "";
        public float r;
        public float g;
        public float b;
        public float a = 1f;
    }

    [Serializable]
    public sealed class LilToonTextureProperty
    {
        public string name = "";
        public int textureIndex = -1;
        public string semantic = "";
        public float scaleX = 1f;
        public float scaleY = 1f;
        public float offsetX;
        public float offsetY;
    }
}
