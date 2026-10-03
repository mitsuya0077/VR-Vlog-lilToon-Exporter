using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    // Keep this attachable avatar probe in the runtime-capable test assembly.
    // Unity refuses to attach a MonoBehaviour imported from an Editor assembly.
    public sealed class NdmfSharedAssetHolder : MonoBehaviour
    {
        public NdmfSharedAssetFixture Settings;
        public Renderer Renderer;
    }
}
