using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // Only avatars with this explicit marker receive ARKit custom expressions.
    [DisallowMultipleComponent]
    public sealed class VrmTrackingMarker : MonoBehaviour
    {
        public VrmTrackingProfile profile;
    }
}
