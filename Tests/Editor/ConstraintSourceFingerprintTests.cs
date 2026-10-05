using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ConstraintSourceFingerprintTests
    {
        [TestCase("rotation")][TestCase("aim")]
        public void FullyDrivenRotationDoesNotInvalidateButOtherTransformChannelsRemainExact(string kind)
        {
            using var f = new Fixture(kind);
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            f.Target.localRotation = Quaternion.Euler(35, 70, -20);
            Assert.That(stamp.Matches(f.Source), Is.True, "A locked full-weight solver owns all rotation channels, regardless of how long it evaluates.");
            f.Target.localPosition += Vector3.right * .001f;
            Assert.That(stamp.Matches(f.Source), Is.False, "The solver does not own this position.");
            stamp = ExportRecoverySourceStamp.Capture(f.Source);
            f.Target.localScale += Vector3.up * .001f;
            Assert.That(stamp.Matches(f.Source), Is.False);
        }

        [TestCase("disabled")][TestCase("inactive")][TestCase("unlocked")][TestCase("partialWeight")][TestCase("partialAxes")]
        public void RotationRemainsAnExactInputWhenTheSolverDoesNotFullyOwnIt(string condition)
        {
            using var f = new Fixture("rotation");
            var constraint = (RotationConstraint)f.Constraint;
            switch (condition)
            {
                case "disabled": constraint.enabled = false; break;
                case "inactive": constraint.constraintActive = false; break;
                case "unlocked": constraint.locked = false; break;
                case "partialWeight": constraint.weight = .5f; break;
                case "partialAxes": constraint.rotationAxis = Axis.X; break;
            }
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            f.Target.localRotation = Quaternion.Euler(0, 1, 0);
            Assert.That(stamp.Matches(f.Source), Is.False);
        }

        [TestCase("offset")][TestCase("rest")][TestCase("sourcePose")][TestCase("sourceReference")]
        [TestCase("weight")][TestCase("axes")][TestCase("enabled")][TestCase("locked")]
        public void AuthoredConstraintInputsStillInvalidateTheCapturedSource(string change)
        {
            using var f = new Fixture("rotation");
            var constraint = (RotationConstraint)f.Constraint;
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            switch (change)
            {
                case "offset": constraint.rotationOffset = new Vector3(0, .001f, 0); break;
                case "rest": constraint.rotationAtRest = new Vector3(0, .001f, 0); break;
                case "sourcePose": f.Anchor.localRotation = Quaternion.Euler(0, .001f, 0); break;
                case "sourceReference": constraint.SetSource(0, new ConstraintSource { sourceTransform = f.Source.transform, weight = 1 }); break;
                case "weight": constraint.weight = .99f; break;
                case "axes": constraint.rotationAxis = Axis.X | Axis.Y; break;
                case "enabled": constraint.enabled = false; break;
                case "locked": constraint.locked = false; break;
            }
            Assert.That(stamp.Matches(f.Source), Is.False, change);
        }

        [Test]
        public void ExternalConstraintInputPoseRemainsGuarded()
        {
            using var f = new Fixture("rotation");
            var external = new GameObject("External constraint source");
            try
            {
                ((RotationConstraint)f.Constraint).SetSource(0, new ConstraintSource { sourceTransform = external.transform, weight = 1 });
                var stamp = ExportRecoverySourceStamp.Capture(f.Source);
                external.transform.localRotation = Quaternion.Euler(0, .001f, 0);
                Assert.That(stamp.Matches(f.Source), Is.False, "A reference identity alone does not describe a constraint's external pose input.");
            }
            finally { Object.DestroyImmediate(external); }
        }

        [Test]
        public void AimWithoutDefinedUpRetainsAnExactAuthoredRotation()
        {
            using var f = new Fixture("aim");
            ((AimConstraint)f.Constraint).worldUpType = AimConstraint.WorldUpType.None;
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            f.Target.localRotation = Quaternion.Euler(0, 0, 10);
            Assert.That(stamp.Matches(f.Source), Is.False, "No-up aiming retains roll from the target orientation.");
        }

        [Test]
        public void DrivenAvatarRootStillTracksItsParentAndOtherLocalChannels()
        {
            var source = new GameObject("Driven avatar root");
            var parent = new GameObject("Source parent");
            var anchor = new GameObject("External rotation input");
            try
            {
                source.transform.SetParent(parent.transform, false);
                var constraint = source.AddComponent<RotationConstraint>();
                constraint.AddSource(new ConstraintSource { sourceTransform = anchor.transform, weight = 1 });
                constraint.rotationAxis = Axis.X | Axis.Y | Axis.Z; constraint.weight = 1; constraint.locked = true; constraint.constraintActive = true;
                var stamp = ExportRecoverySourceStamp.Capture(source);
                source.transform.localRotation = Quaternion.Euler(10, 20, 30);
                Assert.That(stamp.Matches(source), Is.True);
                parent.transform.position = Vector3.right;
                Assert.That(stamp.Matches(source), Is.False);
                stamp = ExportRecoverySourceStamp.Capture(source);
                source.transform.localPosition = Vector3.up;
                Assert.That(stamp.Matches(source), Is.False);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(parent); Object.DestroyImmediate(anchor); }
        }

        [Test]
        public void PrefabOverrideBookkeepingCannotReintroduceTheSolverOutput()
        {
            using var f = new Fixture("rotation");
            var path = "Assets/ConstraintFingerprint-" + Guid.NewGuid().ToString("N") + ".prefab";
            GameObject instance = null;
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(f.Source, path);
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var target = instance.transform.Find("Driven target");
                var stamp = ExportRecoverySourceStamp.Capture(instance);
                target.localRotation = Quaternion.Euler(0, 45, 0);
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                Assert.That(PrefabUtility.GetPropertyModifications(instance).Any(p => p.propertyPath.StartsWith("m_LocalRotation", StringComparison.Ordinal)), Is.True);
                Assert.That(stamp.Matches(instance), Is.True, "Recording derived output as an override is not an authored input change.");
                target.localPosition += Vector3.up;
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                Assert.That(stamp.Matches(instance), Is.False, "Effective prefab overrides still invalidate through their actual component values.");
                stamp = ExportRecoverySourceStamp.Capture(instance);
                var instanceId = instance.GetInstanceID();
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                Assert.That(instance.GetInstanceID(), Is.EqualTo(instanceId));
                Assert.That(stamp.Matches(instance), Is.False, "Prefab identity is an input to authoring tools even when effective property values stay unchanged.");
            }
            finally { if (instance != null) Object.DestroyImmediate(instance); AssetDatabase.DeleteAsset(path); }
        }

        [TestCase("VRCAimConstraint")][TestCase("VRCRotationConstraint")]
        public void InstalledVrcSolverOutputAndExecutionCachesAreDerivedButSettingsRemainGuarded(string typeName)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Dynamics.Constraint.Components." + typeName, false)).FirstOrDefault(t => t != null);
            if (type == null) Assert.Ignore("This SDK does not install " + typeName + ".");
            var source = new GameObject("VRC constraint source");
            try
            {
                var target = new GameObject("Driven target").transform; target.SetParent(source.transform, false);
                var anchor = new GameObject("Constraint input").transform; anchor.SetParent(source.transform, false); anchor.localPosition = Vector3.forward;
                var component = source.AddComponent(type);
                using (var settings = new SerializedObject(component))
                {
                    settings.FindProperty("IsActive").boolValue = true; settings.FindProperty("Locked").boolValue = true;
                    settings.FindProperty("GlobalWeight").floatValue = 1; settings.FindProperty("FreezeToWorld").boolValue = false;
                    settings.FindProperty("AffectsRotationX").boolValue = true; settings.FindProperty("AffectsRotationY").boolValue = true; settings.FindProperty("AffectsRotationZ").boolValue = true;
                    settings.FindProperty("TargetTransform").objectReferenceValue = target;
                    settings.FindProperty("Sources.totalLength").intValue = 1;
                    settings.FindProperty("Sources.source0.SourceTransform").objectReferenceValue = anchor;
                    settings.FindProperty("Sources.source0.Weight").floatValue = 1;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
                var stamp = ExportRecoverySourceStamp.Capture(source);
                target.localRotation = Quaternion.Euler(20, 50, 80);
                using (var settings = new SerializedObject(component))
                {
                    settings.FindProperty("cachedExecutionGroupIndex").intValue = 19;
                    settings.FindProperty("latestValidExecutionGroupIndex").intValue = 19;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
                Assert.That(stamp.Matches(source), Is.True);
                using (var settings = new SerializedObject(component))
                {
                    settings.FindProperty("RotationOffset").vector3Value = Vector3.up * .001f;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
                Assert.That(stamp.Matches(source), Is.False);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [UnityTest]
        public IEnumerator FailedExportChoicesSurviveSolverUpdatesAndStillRejectAnAuthoredChange()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var target = new GameObject("Driven target").transform; target.SetParent(f.Source.transform, false);
            var constraint = target.gameObject.AddComponent<RotationConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = f.Source.transform, weight = 1 });
            constraint.rotationAxis = Axis.X | Axis.Y | Axis.Z; constraint.weight = 1; constraint.locked = true; constraint.constraintActive = true;
            f.Material.SetFloat("_UseAudioLink", 1);
            var failure = ExportRecoveryFailure.At(new NotSupportedException("AudioLink"), "audio-link", f.Material);
            var session = ExportRecoverySession.FromFailedExport(f.Source, "unused.vrm", (options, report, warnings) => new byte[] { 1 },
                failure, ExportRecoveryReport.FromException(f.Source, failure));
            ExportFailureWindow window = null;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                ExportFailureWindow.Show(session);
                window = Resources.FindObjectsOfTypeAll<ExportFailureWindow>().Single(w => ReferenceEquals(typeof(ExportFailureWindow).GetField("session", fields).GetValue(w), session));
                var selected = (HashSet<string>)typeof(ExportFailureWindow).GetField("selected", fields).GetValue(window);
                var action = session.AvailableDiagnostics.Single().Action;
                selected.Add(action.Id);
                for (var tick = 0; tick < 3; tick++)
                {
                    target.localRotation = Quaternion.Euler(0, 30 * (tick + 1), 0);
                    yield return null;
                    typeof(ExportFailureWindow).GetField("nextSourceCheck", fields).SetValue(window, 0d);
                    typeof(ExportFailureWindow).GetMethod("CheckSource", fields).Invoke(window, null);
                    Assert.That(session.IsInvalidated, Is.False);
                    Assert.That(window.BuildSelectedOptions().Actions.Single().Id, Is.EqualTo(action.Id));
                }
                Assert.That(session.Attempt(window.BuildSelectedOptions()), Is.True);
                Assert.That(f.Material.GetFloat("_UseAudioLink"), Is.EqualTo(1));
                constraint.rotationOffset = new Vector3(0, .001f, 0);
                Assert.That(session.CheckForChanges(), Is.True);
                Assert.That(session.AvailableDiagnostics, Is.Empty);
                Assert.Throws<InvalidOperationException>(() => session.SavePending());
            }
            finally { if (window != null) window.Close(); }
        }

        [Test]
        public void PartialVrcQuaternionReencodingAndEquivalentSignKeepTheOriginalCaptureCurrent()
        {
            using var f = new VrcFixture();
            f.Target.localRotation = Quaternion.Euler(10, 20, 30);
            var original = f.Target.localRotation;
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            var reencoded = original;
            reencoded.x = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(reencoded.x) + 1);
            f.Target.localRotation = reencoded;
            Assert.That(stamp.Matches(f.Source), Is.True, "A one-ULP quaternion encoding change is not a new unmasked Euler input.");
            f.Target.localRotation = new Quaternion(-original.x, -original.y, -original.z, -original.w);
            Assert.That(stamp.Matches(f.Source), Is.True, "Quaternion signs represent the same rotation.");
            f.Target.localRotation = original * Quaternion.Euler(.001f, 0, 0);
            Assert.That(stamp.Matches(f.Source), Is.False, "An authored unmasked-axis change remains guarded.");
        }

        [Test]
        public void PartialVrcToleranceNeverWalksItsOriginalBaseline()
        {
            using var f = new VrcFixture();
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            f.Target.localRotation = Quaternion.Euler(0, .000005f, 0);
            Assert.That(stamp.Matches(f.Source), Is.True);
            f.Target.localRotation = Quaternion.Euler(0, .00001f, 0);
            Assert.That(stamp.Matches(f.Source), Is.True);
            f.Target.localRotation = Quaternion.Euler(0, .000015f, 0);
            Assert.That(stamp.Matches(f.Source), Is.False, "Small repeated changes must be compared with the first capture, not the previous check.");
        }

        [Test]
        public void InvalidatedPartialConstraintSessionNeverRevivesWhenThePoseReturns()
        {
            using var f = new VrcFixture();
            var session = new ExportRecoverySession(f.Source, "unused.vrm", (options, report, warnings) => new byte[] { 1 });
            Assert.That(session.Attempt(null), Is.True);
            f.Target.localRotation = Quaternion.Euler(.001f, 0, 0);
            Assert.That(session.CheckForChanges(), Is.True);
            f.Target.localRotation = Quaternion.identity;
            Assert.That(session.CheckForChanges(), Is.True);
            Assert.That(session.CanSave, Is.False);
        }

        [Test]
        public void EmptyNativeConstraintCannotHideAnAuthoredRotation()
        {
            using var f = new Fixture("rotation");
            ((RotationConstraint)f.Constraint).RemoveSource(0);
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            f.Target.localRotation = Quaternion.Euler(0, .000005f, 0);
            Assert.That(stamp.Matches(f.Source), Is.False);
        }

        [TestCase("offset")][TestCase("rest")][TestCase("hint")][TestCase("sourcePose")][TestCase("sourceReference")]
        public void PartialVrcAuthoredInputsRemainExact(string change)
        {
            using var f = new VrcFixture();
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            if (change == "sourcePose") f.Anchor.localRotation = Quaternion.Euler(.001f, 0, 0);
            else
            {
                using var settings = new SerializedObject(change == "hint" ? (Object)f.Target : f.Component);
                if (change == "sourceReference") settings.FindProperty("Sources.source0.SourceTransform").objectReferenceValue = f.Source.transform;
                else settings.FindProperty(change == "offset" ? "RotationOffset" : change == "rest" ? "RotationAtRest" : "m_LocalEulerAnglesHint").vector3Value = Vector3.right * .000001f;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            Assert.That(stamp.Matches(f.Source), Is.False);
        }

        [TestCase("noSources")][TestCase("zeroSourceWeight")][TestCase("zeroWeight")][TestCase("negativeWeight")]
        [TestCase("unlocked")][TestCase("inactive")][TestCase("frozen")][TestCase("noAxes")]
        public void VrcRotationIsExactWhenTheSolverCannotOwnItsOutput(string condition)
        {
            using var f = new VrcFixture();
            using (var settings = new SerializedObject(f.Component))
            {
                switch (condition)
                {
                    case "noSources": settings.FindProperty("Sources.totalLength").intValue = 0; break;
                    case "zeroSourceWeight": settings.FindProperty("Sources.source0.Weight").floatValue = 0; break;
                    case "zeroWeight": settings.FindProperty("GlobalWeight").floatValue = 0; break;
                    case "negativeWeight": settings.FindProperty("GlobalWeight").floatValue = -.5f; break;
                    case "unlocked": settings.FindProperty("Locked").boolValue = false; break;
                    case "inactive": settings.FindProperty("IsActive").boolValue = false; break;
                    case "frozen": settings.FindProperty("FreezeToWorld").boolValue = true; break;
                    case "noAxes": settings.FindProperty("AffectsRotationY").boolValue = false; break;
                }
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            var stamp = ExportRecoverySourceStamp.Capture(f.Source);
            f.Target.localRotation = Quaternion.Euler(0, .000005f, 0);
            Assert.That(stamp.Matches(f.Source), Is.False);
        }

        [TestCase("zero")][TestCase("nan")][TestCase("infinite")]
        public void PartialRotationComparisonRejectsMalformedQuaternions(string kind)
        {
            var source = new GameObject("Rotation comparison");
            try
            {
                var first = new ExportSourceFingerprint.PartialRotation(source.transform);
                var other = new ExportSourceFingerprint.PartialRotation(source.transform);
                var value = kind == "zero" ? new Quaternion(0, 0, 0, 0) : new Quaternion(kind == "nan" ? float.NaN : float.PositiveInfinity, 0, 0, 1);
                typeof(ExportSourceFingerprint.PartialRotation).GetField("rotation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(other, value);
                Assert.That(first.Matches(other), Is.False);
            }
            finally { Object.DestroyImmediate(source); }
        }

        sealed class VrcFixture : IDisposable
        {
            internal readonly GameObject Source = new GameObject("Partial constraint source");
            internal readonly Transform Anchor, Target;
            internal readonly Component Component;
            internal VrcFixture()
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint", false)).FirstOrDefault(t => t != null);
                if (type == null) { Object.DestroyImmediate(Source); Assert.Ignore("The VRC constraint SDK is not installed."); }
                Anchor = new GameObject("Authored input").transform; Anchor.SetParent(Source.transform, false);
                Target = new GameObject("Partially driven target").transform; Target.SetParent(Source.transform, false);
                Component = Target.gameObject.AddComponent(type);
                using var settings = new SerializedObject(Component);
                settings.FindProperty("IsActive").boolValue = true; settings.FindProperty("Locked").boolValue = true;
                settings.FindProperty("GlobalWeight").floatValue = .5f; settings.FindProperty("FreezeToWorld").boolValue = false;
                settings.FindProperty("AffectsRotationX").boolValue = false; settings.FindProperty("AffectsRotationY").boolValue = true; settings.FindProperty("AffectsRotationZ").boolValue = false;
                settings.FindProperty("Sources.totalLength").intValue = 1;
                settings.FindProperty("Sources.source0.SourceTransform").objectReferenceValue = Anchor;
                settings.FindProperty("Sources.source0.Weight").floatValue = 1;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            public void Dispose() { if (Source != null) Object.DestroyImmediate(Source); }
        }

        sealed class Fixture : IDisposable
        {
            internal readonly GameObject Source = new GameObject("Constraint source");
            internal readonly Transform Anchor, Target;
            internal readonly Behaviour Constraint;
            internal Fixture(string kind)
            {
                Anchor = new GameObject("Authored source").transform; Anchor.SetParent(Source.transform, false); Anchor.localPosition = Vector3.forward;
                Target = new GameObject("Driven target").transform; Target.SetParent(Source.transform, false);
                if (kind == "aim")
                {
                    var value = Target.gameObject.AddComponent<AimConstraint>(); Constraint = value;
                    value.AddSource(new ConstraintSource { sourceTransform = Anchor, weight = 1 });
                    value.rotationAxis = Axis.X | Axis.Y | Axis.Z; value.weight = 1; value.locked = true; value.constraintActive = true;
                }
                else
                {
                    var value = Target.gameObject.AddComponent<RotationConstraint>(); Constraint = value;
                    value.AddSource(new ConstraintSource { sourceTransform = Anchor, weight = 1 });
                    value.rotationAxis = Axis.X | Axis.Y | Axis.Z; value.weight = 1; value.locked = true; value.constraintActive = true;
                }
            }
            public void Dispose() { Object.DestroyImmediate(Source); }
        }
    }
}
