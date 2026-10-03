using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class NdmfSharedAssetFixture : ScriptableObject
    {
        public Mesh Mesh;
        public Material Material;
        public AnimationClip Clip;
        public NdmfSharedAssetFixture Next;
        public Object Target;
        public int Value;
    }
}
