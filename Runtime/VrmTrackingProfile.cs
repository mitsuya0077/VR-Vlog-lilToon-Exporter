using System;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    [Serializable]
    public sealed class TrackingMorph
    {
        public string shape;
        [Range(0.01f, 1f)] public float weight = 1f;
    }

    [Serializable]
    public sealed class TrackingExpression
    {
        public string name;
        public TrackingMorph[] morphs = Array.Empty<TrackingMorph>();
    }

    [Serializable]
    public sealed class VrcTrackingChannel
    {
        public string parameter;
        public string negativeShape;
        public string positiveShape;
        public float center;
        public float minimum = -1f;
        public float maximum = 1f;
        [Range(1f, 100f)] public float maxBlendShapeWeight = 100f;
    }

    [CreateAssetMenu(menuName = "VR Vlog/Face Tracking Profile")]
    public sealed class VrmTrackingProfile : ScriptableObject
    {
        public TrackingExpression[] expressions = Array.Empty<TrackingExpression>();
        public VrcTrackingChannel[] vrcChannels = Array.Empty<VrcTrackingChannel>();
    }

}
