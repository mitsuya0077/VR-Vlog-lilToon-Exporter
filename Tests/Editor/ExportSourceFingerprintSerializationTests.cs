using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportSourceFingerprintSerializationTests
    {
        [TestCase("floatCurve")]
        [TestCase("objectReference")]
        [TestCase("animationEvent")]
        [TestCase("clipSettings")]
        public void AuthoredImportedClipChangesRemainGuardedAfterItsEditorCacheWarms(string change)
        {
            using var f = new SourceFingerprintTests.ColdAnimationClipFixture();
            Assert.That(f.EditorCurveCount(), Is.Zero);
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            AnimationUtility.GetCurveBindings(f.Clip);
            AnimationUtility.GetObjectReferenceCurveBindings(f.Clip);
            Assert.That(f.EditorCurveCount(), Is.GreaterThan(0));
            Assert.That(stamp.Matches(f.Source), Is.True, "The initial read-only cache expansion must remain current.");
            switch (change)
            {
                case "floatCurve":
                    var curve = AnimationUtility.GetEditorCurve(f.Clip, f.FloatBinding);
                    var key = curve[1]; key.value += 5; curve.MoveKey(1, key);
                    AnimationUtility.SetEditorCurve(f.Clip, f.FloatBinding, curve);
                    break;
                case "objectReference":
                    AnimationUtility.SetObjectReferenceCurve(f.Clip, f.ObjectBinding,
                        new[] { new ObjectReferenceKeyframe { time = 0, value = f.OtherMaterial } });
                    break;
                case "animationEvent":
                    var events = AnimationUtility.GetAnimationEvents(f.Clip);
                    Assert.That(events, Has.Length.EqualTo(1));
                    events[0].intParameter++;
                    AnimationUtility.SetAnimationEvents(f.Clip, events);
                    break;
                case "clipSettings":
                    var settings = AnimationUtility.GetAnimationClipSettings(f.Clip);
                    settings.loopTime = !settings.loopTime;
                    AnimationUtility.SetAnimationClipSettings(f.Clip, settings);
                    break;
                default: Assert.Fail(change); break;
            }
            Assert.That(stamp.Matches(f.Source), Is.False, "Warming native caches must not hide an authored clip change: " + change);
            Assert.That(ExportRecoverySourceStamp.Capture(f.Source).Matches(f.Source), Is.True);
            Assert.That(f.Source.GetComponent<Animator>().runtimeAnimatorController, Is.SameAs(f.Controller));
            Assert.That(f.Controller.layers[0].stateMachine.defaultState.motion, Is.SameAs(f.Clip));
        }

        [TestCase("bool")][TestCase("int")][TestCase("signed64")][TestCase("unsigned64")]
        [TestCase("doubleUlp")][TestCase("doubleSignedZero")][TestCase("float")][TestCase("char")]
        [TestCase("enum")][TestCase("layerMask")][TestCase("hash128")][TestCase("color")]
        [TestCase("vector2")][TestCase("vector3")][TestCase("vector4")][TestCase("quaternion")]
        [TestCase("rect")][TestCase("bounds")][TestCase("vector2Int")][TestCase("vector3Int")]
        [TestCase("rectInt")][TestCase("boundsInt")][TestCase("arrayValue")][TestCase("arraySize")]
        [TestCase("nullString")]
        public void UnsavedPayloadValuesInvalidateWithoutMutatingTheSource(string change)
        {
            using var f = new Fixture();
            var asset = f.Asset;
            if (change == "doubleSignedZero") asset.Precise = 0d;
            var stamp = f.CaptureStable();
            switch (change)
            {
                case "bool": asset.Enabled = !asset.Enabled; break;
                case "int": asset.Integer++; break;
                case "signed64": asset.Signed++; break;
                case "unsigned64": asset.Unsigned++; break;
                case "doubleUlp": asset.Precise = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(asset.Precise) + 1); break;
                case "doubleSignedZero": asset.Precise = BitConverter.Int64BitsToDouble(long.MinValue); break;
                case "float": asset.Fraction += .125f; break;
                case "char": asset.Character = 'z'; break;
                case "enum": asset.Mode = SourceFingerprintPayloadMode.Second; break;
                case "layerMask": asset.Layers = 1 << 9; break;
                case "hash128": asset.Hash = new Hash128(1, 2, 3, 5); break;
                case "color": asset.Color = new Color(.3f, .4f, .5f, .6f); break;
                case "vector2": asset.Vector2 += Vector2.one; break;
                case "vector3": asset.Vector3 += Vector3.up; break;
                case "vector4": asset.Vector4 += Vector4.one; break;
                case "quaternion": asset.Rotation = Quaternion.Euler(15, 25, 35); break;
                case "rect": asset.Rect = new Rect(1, 2, 3, 5); break;
                case "bounds": asset.Bounds = new Bounds(Vector3.up, Vector3.one * 3); break;
                case "vector2Int": asset.Vector2Int += Vector2Int.one; break;
                case "vector3Int": asset.Vector3Int += Vector3Int.one; break;
                case "rectInt": asset.RectInt = new RectInt(1, 2, 3, 5); break;
                case "boundsInt": asset.BoundsInt = new BoundsInt(Vector3Int.up, Vector3Int.one * 3); break;
                case "arrayValue": asset.Numbers[1]++; break;
                case "arraySize": asset.Numbers = new[] { 1, 2, 3, 4 }; break;
                case "nullString": asset.Text = null; break;
                default: Assert.Fail(change); break;
            }
            f.AssertChanged(stamp);
        }

        [TestCase("color")][TestCase("colorTime")][TestCase("alpha")][TestCase("alphaTime")]
        [TestCase("mode")][TestCase("colorSpace")]
        public void GradientKeysAndEvaluationSettingsRemainTracked(string change)
        {
            using var f = new Fixture();
            var stamp = f.CaptureStable();
            var gradient = f.Asset.Gradient;
            if (change == "color" || change == "colorTime")
            {
                var keys = gradient.colorKeys;
                if (change == "color") keys[1].color = Color.green;
                else keys[1].time = .6f;
                gradient.colorKeys = keys;
            }
            if (change == "alpha" || change == "alphaTime")
            {
                var keys = gradient.alphaKeys;
                if (change == "alpha") keys[1].alpha = .2f;
                else keys[1].time = .6f;
                gradient.alphaKeys = keys;
            }
            if (change == "mode") gradient.mode = GradientMode.Fixed;
            if (change == "colorSpace") gradient.colorSpace = ColorSpace.Gamma;
            f.AssertChanged(stamp);
        }

        [TestCase("preWrap")][TestCase("postWrap")][TestCase("time")][TestCase("value")]
        [TestCase("inTangent")][TestCase("outTangent")][TestCase("inWeight")][TestCase("outWeight")]
        [TestCase("weightedMode")][TestCase("leftMode")][TestCase("rightMode")][TestCase("broken")]
        public void WeightedCurveAndEditorTangentSettingsRemainTracked(string change)
        {
            using var f = new Fixture();
            var stamp = f.CaptureStable();
            var curve = f.Asset.Curve;
            if (change == "preWrap") curve.preWrapMode = WrapMode.Loop;
            else if (change == "postWrap") curve.postWrapMode = WrapMode.PingPong;
            else if (change == "leftMode") AnimationUtility.SetKeyLeftTangentMode(curve, 1, AnimationUtility.TangentMode.Constant);
            else if (change == "rightMode") AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Constant);
            else if (change == "broken") AnimationUtility.SetKeyBroken(curve, 0, false);
            else
            {
                var index = change.StartsWith("in", StringComparison.Ordinal) ? 1 : 0;
                var key = curve[index];
                switch (change)
                {
                    case "time": key.time = .1f; break;
                    case "value": key.value = .3f; break;
                    case "inTangent": key.inTangent = .75f; break;
                    case "outTangent": key.outTangent = .75f; break;
                    case "inWeight": key.inWeight = .15f; break;
                    case "outWeight": key.outWeight = .15f; break;
                    case "weightedMode": key.weightedMode = WeightedMode.None; break;
                    default: Assert.Fail(change); break;
                }
                curve.MoveKey(index, key);
            }
            f.AssertChanged(stamp);
        }

        [TestCase(1023)][TestCase(2047)][TestCase(8191)]
        public void LongUnicodeTextIsStableAcrossEncoderChunksAndDetectsAChangedSurrogatePair(int prefix)
        {
            using var f = new Fixture();
            // The first high surrogate lands at a 1024-character chunk boundary.
            var text = new string('x', prefix) + "\uD83D\uDE00" + new string('y', 65536) + "\0\u65E5\u672C\u8A9E";
            f.Asset.Text = text;
            var stamp = f.CaptureStable();
            f.Asset.Text = new string('x', prefix) + "\uD83D\uDE03" + text.Substring(prefix + 2);
            f.AssertChanged(stamp);
        }

        [Test]
        public void AdjacentTextFieldsKeepTheirBoundariesInTheDigest()
        {
            using var f = new Fixture();
            f.Asset.Text = "ab"; f.Asset.SecondText = "c";
            var stamp = f.CaptureStable();
            f.Asset.Text = "a"; f.Asset.SecondText = "bc";
            f.AssertChanged(stamp);
        }

        [TestCase("self")][TestCase("mutual")][TestCase("shared")]
        public void CyclicAndSharedManagedGraphsTerminateAndDetectNestedMutations(string shape)
        {
            using var f = new Fixture();
            var first = new SourceFingerprintManagedNode { Value = 11 };
            var second = shape == "self" ? first : new SourceFingerprintManagedNode { Value = 22 };
            first.Next = second; second.Next = first;
            f.Probe.Graph = first;
            if (shape == "shared") f.Probe.SharedGraph = new[] { second, second, first };
            // Unity API stays on the Editor main thread. The elapsed bound is
            // evidence of termination, not a separate process watchdog.
            var clock = Stopwatch.StartNew();
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            Assert.That(stamp.Matches(f.Source), Is.True);
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
            Assert.That(first.Next, Is.SameAs(second)); Assert.That(second.Next, Is.SameAs(first));
            second.Value++;
            f.AssertChanged(stamp);
        }

        [Test]
        public void ManagedDynamicTypeAndSharingTopologyArePartOfTheCapturedInput()
        {
            using var f = new Fixture();
            var shared = new SourceFingerprintManagedNode { Value = 17 };
            f.Probe.Graph = shared; f.Probe.SharedGraph = new[] { shared, shared };
            var stamp = f.CaptureStable();
            f.Probe.SharedGraph[1] = new SourceFingerprintManagedNode { Value = shared.Value };
            f.AssertChanged(stamp);
            stamp = f.CaptureStable();
            f.Probe.Graph = new SourceFingerprintManagedAlternate { Value = shared.Value };
            f.AssertChanged(stamp);
        }

        [Test]
        public void UnsavedCyclicUnityAssetGraphTracksNestedValuesAndReferenceReplacement()
        {
            using var f = new Fixture();
            var other = f.NewAsset(); var replacement = f.NewAsset();
            f.Asset.Other = other; other.Other = f.Asset; replacement.Other = f.Asset;
            f.Probe.References = new[] { f.Asset, other, other };
            var stamp = f.CaptureStable();
            other.Signed++;
            f.AssertChanged(stamp);
            stamp = f.CaptureStable();
            f.Probe.References[1] = replacement;
            f.AssertChanged(stamp);
            Assert.That(f.Asset.Other, Is.SameAs(other)); Assert.That(other.Other, Is.SameAs(f.Asset));
        }

        [TestCase("default")][TestCase("nullDefault")][TestCase("name")]
        public void ExposedReferenceDefaultAndNameRemainPartOfTheCapturedInput(string change)
        {
            using var f = new Fixture();
            f.Probe.Exposed = new ExposedReference<SourceFingerprintPayloadAsset>
            { defaultValue = f.Asset, exposedName = new PropertyName("initial-reference") };
            var stamp = f.CaptureStable();
            var exposed = f.Probe.Exposed;
            if (change == "default") exposed.defaultValue = f.NewAsset();
            if (change == "nullDefault") exposed.defaultValue = null;
            if (change == "name") exposed.exposedName = new PropertyName("other-reference");
            f.Probe.Exposed = exposed;
            f.AssertChanged(stamp);
        }

        sealed class Fixture : IDisposable
        {
            internal readonly GameObject Source = new GameObject("Serialization fingerprint source");
            internal readonly SourceFingerprintPayloadProbe Probe;
            internal readonly SourceFingerprintPayloadAsset Asset;
            readonly List<SourceFingerprintPayloadAsset> owned = new List<SourceFingerprintPayloadAsset>();

            internal Fixture()
            {
                Source.transform.localPosition = new Vector3(.1f, .2f, .3f);
                Probe = Source.AddComponent<SourceFingerprintPayloadProbe>();
                Asset = NewAsset(); Probe.Asset = Asset;
            }

            internal SourceFingerprintPayloadAsset NewAsset()
            {
                var asset = ScriptableObject.CreateInstance<SourceFingerprintPayloadAsset>(); owned.Add(asset);
                asset.Text = "source value"; asset.SecondText = "second value"; asset.Enabled = true; asset.Integer = 11;
                asset.Signed = long.MaxValue - 3; asset.Unsigned = ulong.MaxValue - 3; asset.Precise = 1d; asset.Fraction = .25f;
                asset.Character = 'a'; asset.Layers = 1 << 3; asset.Hash = new Hash128(1, 2, 3, 4);
                asset.Color = new Color(.1f, .2f, .3f, .4f); asset.Vector2 = new Vector2(1, 2); asset.Vector3 = new Vector3(1, 2, 3);
                asset.Vector4 = new Vector4(1, 2, 3, 4); asset.Rotation = Quaternion.Euler(5, 10, 15);
                asset.Rect = new Rect(1, 2, 3, 4); asset.Bounds = new Bounds(Vector3.zero, Vector3.one);
                asset.Vector2Int = new Vector2Int(1, 2); asset.Vector3Int = new Vector3Int(1, 2, 3);
                asset.RectInt = new RectInt(1, 2, 3, 4); asset.BoundsInt = new BoundsInt(Vector3Int.zero, Vector3Int.one);
                asset.Numbers = new[] { 1, 2, 3 };
                asset.Gradient = new Gradient { mode = GradientMode.Blend, colorSpace = ColorSpace.Linear };
                asset.Gradient.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.blue, .4f), new GradientColorKey(Color.white, 1) },
                    new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(.5f, .4f), new GradientAlphaKey(1, 1) });
                asset.Curve = new AnimationCurve(
                    new Keyframe(0, .1f, .2f, .3f, .25f, .4f) { weightedMode = WeightedMode.Both },
                    new Keyframe(1, .9f, .4f, .5f, .35f, .45f) { weightedMode = WeightedMode.Both });
                asset.Curve.preWrapMode = WrapMode.ClampForever; asset.Curve.postWrapMode = WrapMode.ClampForever;
                for (var i = 0; i < asset.Curve.length; i++)
                {
                    AnimationUtility.SetKeyBroken(asset.Curve, i, true);
                    AnimationUtility.SetKeyLeftTangentMode(asset.Curve, i, AnimationUtility.TangentMode.Free);
                    AnimationUtility.SetKeyRightTangentMode(asset.Curve, i, AnimationUtility.TangentMode.Free);
                }
                return asset;
            }

            internal ExportRecoverySourceStamp CaptureStable()
            {
                var assetJson = EditorJsonUtility.ToJson(Asset);
                var probeJson = EditorJsonUtility.ToJson(Probe);
                var local = Source.transform.localPosition;
                var stamp = ExportRecoverySourceStamp.Capture(Source);
                Assert.That(stamp.Matches(Source), Is.True); Assert.That(stamp.Matches(Source), Is.True);
                Assert.That(EditorJsonUtility.ToJson(Asset), Is.EqualTo(assetJson), "Small fixture payloads must not be rewritten while fingerprinting.");
                Assert.That(EditorJsonUtility.ToJson(Probe), Is.EqualTo(probeJson));
                Assert.That(Source.transform.localPosition, Is.EqualTo(local)); Assert.That(Probe.Asset, Is.SameAs(Asset));
                return stamp;
            }

            internal void AssertChanged(ExportRecoverySourceStamp stamp)
            {
                Assert.That(stamp.Matches(Source), Is.False);
                var current = ExportRecoverySourceStamp.Capture(Source);
                Assert.That(current.Matches(Source), Is.True);
                Assert.That(Probe.Asset, Is.SameAs(Asset));
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Source);
                foreach (var asset in owned) if (asset != null) Object.DestroyImmediate(asset);
            }
        }
    }
}
