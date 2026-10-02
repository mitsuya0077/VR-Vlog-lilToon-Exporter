using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    // An avatar authoring script can run synchronously during Instantiate,
    // before an export has had a chance to separate its shared materials.
    [ExecuteAlways]
    public sealed class RecoveryCallbackProbe : MonoBehaviour
    {
        public Material SharedMaterial;
        public static bool Armed;
        public static int AwakeCalls, EnableCalls;

        void Awake() => Observe(true);
        void OnEnable() => Observe(false);

        void Observe(bool awake)
        {
            if (!Armed) return;
            if (awake) AwakeCalls++; else EnableCalls++;
            if (SharedMaterial != null) SharedMaterial.SetColor("_Color", Color.magenta);
        }

        public static void ResetCounters()
        {
            Armed = false;
            AwakeCalls = 0;
            EnableCalls = 0;
        }
    }
}
