using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class PreparedNeutralEligibilityTests
    {
        GameObject source, prepared;
        readonly List<Object> owned = new List<Object>();
        readonly List<Mesh> meshes = new List<Mesh>();

        [SetUp] public void SetUp() => source = new GameObject("Avatar");
        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(prepared); Object.DestroyImmediate(source);
            foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            foreach (var asset in owned) if (asset != null) Object.DestroyImmediate(asset);
            owned.Clear(); meshes.Clear();
        }

        SkinnedMeshRenderer Skin(params string[] names)
        {
            var face = new GameObject("Face"); face.transform.SetParent(source.transform, false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            foreach (var name in names) mesh.AddBlendShapeFrame(name, 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, null, null);
            owned.Add(mesh);
            var skin = face.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
            return skin;
        }

        [Test]
        public void ExportCaptureWaitsForFxOpeningBeforeSelectingUnifiedClosure()
        {
            var original = Skin("UE/EyeClosedLeft", "UE/EyeClosedRight");
            original.SetBlendShapeWeight(0, 100); original.SetBlendShapeWeight(1, 100);
            Assert.Throws<InvalidOperationException>(() => BlinkExportSession.Resolve(source));
            using var captured = BlinkExportSession.CaptureForExport(source);
            prepared = Object.Instantiate(source);
            using var blink = captured.ForClone(source, prepared);
            Assert.DoesNotThrow(() => blink.VerifyPreparedIdentity(prepared));
            NeutralShapeSnapshot.Apply(prepared, new[] {
                new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "UE/EyeClosedLeft", Weight = 0 },
                new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "UE/EyeClosedRight", Weight = 0 }
            });
            blink.ResolvePreparedNeutral(prepared);
            Assert.That(blink.Slots[0].Count, Is.EqualTo(2));
            Assert.That(blink.RequiresUnifiedEvidence, Is.False);
            blink.Bake(prepared, meshes);
            Assert.That(original.GetBlendShapeWeight(0), Is.EqualTo(100));
            Assert.That(original.GetBlendShapeWeight(1), Is.EqualTo(100));
        }

        [Test]
        public void DeferredCaptureStillRejectsMissingBlinkAtPreparedNeutral()
        {
            Skin("Unrelated customization");
            using var captured = BlinkExportSession.CaptureForExport(source);
            prepared = Object.Instantiate(source);
            using var blink = captured.ForClone(source, prepared);
            Assert.DoesNotThrow(() => blink.VerifyPreparedIdentity(prepared));
            Assert.Throws<InvalidOperationException>(() => blink.ResolvePreparedNeutral(prepared));
        }

        [Test]
        public void DeferredCaptureProtectsRendererIdentityAndMovingGeometry()
        {
            var original = Skin("UE/EyeClosed"); original.SetBlendShapeWeight(0, 100);
            using var captured = BlinkExportSession.CaptureForExport(source);
            prepared = Object.Instantiate(source);
            using var blink = captured.ForClone(source, prepared);
            var skin = prepared.GetComponentInChildren<SkinnedMeshRenderer>();
            var altered = Object.Instantiate(skin.sharedMesh); owned.Add(altered);
            altered.ClearBlendShapes(); altered.AddBlendShapeFrame("UE/EyeClosed", 100, new Vector3[3], null, null);
            skin.sharedMesh = altered;
            Assert.Throws<InvalidOperationException>(() => blink.VerifyPreparedIdentity(prepared),
                "A fully resting serialized eyelid still has source geometry that NDMF must preserve.");
        }

        [Test]
        public void PreBuildGuardAllowsWeightChangesButStillProtectsDeformation()
        {
            Skin("UE/JawOpen"); prepared = Object.Instantiate(source);
            var skin = prepared.GetComponentInChildren<SkinnedMeshRenderer>();
            var guard = new UnifiedExpressionPreparation(prepared);
            Assert.That(guard.SupportsUnified, Is.True);
            skin.SetBlendShapeWeight(0, 100);
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            Assert.Throws<InvalidOperationException>(() => guard.Verify(), "Output usability remains relative to the actual neutral.");
            var altered = Object.Instantiate(skin.sharedMesh); owned.Add(altered);
            altered.ClearBlendShapes(); altered.AddBlendShapeFrame("UE/JawOpen", 100, new Vector3[3], null, null);
            skin.sharedMesh = altered;
            Assert.Throws<InvalidOperationException>(() => guard.VerifyIdentityAndDeformation());
        }

        [Test]
        public void AuthoredPartialEndpointIsVerifiedAgainstItsDeclaredAbsoluteWeight()
        {
            var skin = Skin("Custom jaw"); skin.SetBlendShapeWeight(0, 100);
            var vrm = ScriptableObject.CreateInstance<VRM10Object>(); owned.Add(vrm);
            var clip = ScriptableObject.CreateInstance<VRM10Expression>(); owned.Add(clip);
            clip.name = "UE/JawOpen"; clip.MorphTargetBindings = new[] { new MorphTargetBinding("Face", 0, .25f) };
            vrm.Expression.CustomClips.Add(clip); source.AddComponent<Vrm10Instance>().Vrm = vrm;
            var guard = new UnifiedExpressionPreparation(source);
            Assert.That(guard.SupportsUnified, Is.True);
            Assert.DoesNotThrow(() => guard.Verify());
            skin.SetBlendShapeWeight(0, 25);
            Assert.DoesNotThrow(() => guard.VerifyIdentityAndDeformation());
            Assert.Throws<InvalidOperationException>(() => guard.Verify(), "An endpoint equal to the new neutral is inert.");
        }

        [Test]
        public void InferredUnifiedBlinkUsesTheSameFinalSourceFrameAsRawTracking()
        {
            var original = Skin("UE/EyeClosed");
            original.sharedMesh.ClearBlendShapes();
            original.sharedMesh.AddBlendShapeFrame("UE/EyeClosed", 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, null, null);
            original.sharedMesh.AddBlendShapeFrame("UE/EyeClosed", 200, new[] { Vector3.up * 3, Vector3.zero, Vector3.zero }, null, null);
            original.SetBlendShapeWeight(0, 100);
            using var captured = BlinkExportSession.CaptureForExport(source);
            prepared = Object.Instantiate(source);
            using var blink = captured.ForClone(source, prepared);
            blink.VerifyPreparedIdentity(prepared); blink.ResolvePreparedNeutral(prepared);
            Assert.That(blink.Slots[0].Single().Weight, Is.EqualTo(100), "The public UI/manual range remains0..100.");
            blink.Bake(prepared, meshes); AvatarBaseShape.Preserve(prepared, prepared, meshes, null);
            var mesh = prepared.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh;
            var raw = new Vector3[3]; var inferred = new Vector3[3];
            mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("UE/EyeClosed"), 0, raw, null, null);
            mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(blink.Slots[0].Single().Shape), 0, inferred, null, null);
            Assert.That(mesh.vertices[0] + inferred[0], Is.EqualTo(Vector3.up * 3));
            Assert.That(inferred[0], Is.EqualTo(raw[0]), "Automatic closure and raw UE tracking must reach the same final200 frame from neutral100.");
            Assert.That(original.sharedMesh.GetBlendShapeFrameWeight(0, 1), Is.EqualTo(200));
            Assert.That(original.GetBlendShapeWeight(0), Is.EqualTo(100));
        }

        [Test]
        public void PreparedPathMappingKeepsUnderlayOnlyRendererIdentityAcrossMoves()
        {
            Skin("Selected expression");
            var underlay = Skin("Default opening"); underlay.name = "Underlay only";
            prepared = Object.Instantiate(source);
            var menu = new VrChatExpressionMenu.Source();
            var entry = new VrChatExpressionMenu.Entry { Name = "FaceEmo selection" };
            entry.Values.Add(new VrChatExpressionMenu.MorphValue { Path = "Face", Shape = "Selected expression", Weight = 50 });
            menu.Entries.Add(entry);
            var bindings = new PreparedExpressionBindings(prepared, menu);
            var preparedUnderlay = prepared.transform.Find("Underlay only");
            var parent = new GameObject("Generated"); parent.transform.SetParent(prepared.transform, false);
            preparedUnderlay.SetParent(parent.transform, false); preparedUnderlay.name = "Moved underlay";
            Assert.That(bindings.AuthoringPath("Generated/Moved underlay"), Is.EqualTo("Underlay only"));
            Assert.That(bindings.AuthoringPath("Underlay only"), Is.Null);
            entry.Values.Add(new VrChatExpressionMenu.MorphValue {
                Path = bindings.AuthoringPath("Generated/Moved underlay"), Shape = "Default opening", Weight = 100
            });
            bindings.Capture(menu);
            Assert.That(entry.Error, Is.Null);
            Assert.That(bindings.Get("Underlay only").Renderer, Is.SameAs(preparedUnderlay.GetComponent<SkinnedMeshRenderer>()));
            Assert.That(bindings.Get("Underlay only").Mesh, Is.SameAs(underlay.sharedMesh));
        }

        [Test]
        public void PreparedPathMappingCannotChooseBetweenAmbiguousOriginalOrCurrentPaths()
        {
            Skin("Open"); Skin("Open"); prepared = Object.Instantiate(source);
            var bindings = new PreparedExpressionBindings(prepared, new VrChatExpressionMenu.Source());
            prepared.transform.GetChild(0).name = "Unique current path";
            Assert.That(bindings.AuthoringPath("Unique current path"), Is.Null, "The authoring path was ambiguous before NDMF.");
            prepared.transform.GetChild(0).name = "Face";
            Assert.That(bindings.AuthoringPath("Face"), Is.Null, "The prepared path cannot identify one renderer.");
        }
    }
}
