using System;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class SourceFingerprintPayloadProbe : MonoBehaviour
    {
        public SourceFingerprintPayloadAsset Asset;
        public SourceFingerprintPayloadAsset[] References;
        public ExposedReference<SourceFingerprintPayloadAsset> Exposed;
        [SerializeReference] public SourceFingerprintManagedNode Graph;
        [SerializeReference] public SourceFingerprintManagedNode[] SharedGraph;
    }

    public sealed class SourceFingerprintPayloadAsset : ScriptableObject
    {
        public SourceFingerprintPayloadAsset Other;
        public string Text, SecondText;
        public bool Enabled;
        public int Integer;
        public long Signed;
        public ulong Unsigned;
        public double Precise;
        public float Fraction;
        public char Character;
        public SourceFingerprintPayloadMode Mode;
        public LayerMask Layers;
        public Hash128 Hash;
        public Color Color;
        public Vector2 Vector2;
        public Vector3 Vector3;
        public Vector4 Vector4;
        public Quaternion Rotation;
        public Rect Rect;
        public Bounds Bounds;
        public Vector2Int Vector2Int;
        public Vector3Int Vector3Int;
        public RectInt RectInt;
        public BoundsInt BoundsInt;
        public int[] Numbers;
        public Gradient Gradient;
        public AnimationCurve Curve;
    }

    public enum SourceFingerprintPayloadMode { First, Second }

    [Serializable]
    public class SourceFingerprintManagedNode
    {
        public int Value;
        [SerializeReference] public SourceFingerprintManagedNode Next;
    }

    // Identical fields but a different dynamic type must remain distinguishable.
    [Serializable]
    public sealed class SourceFingerprintManagedAlternate : SourceFingerprintManagedNode { }
}
