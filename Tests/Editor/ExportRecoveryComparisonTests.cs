using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class ExportRecoveryComparisonTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReloadedVrmIsFramedFromItsOwnBoundsWhenSourceHasAnOffsetParent(bool transformedParent)
        {
            using var f = new AttachmentConnectionTests.Fixture();
            var parent = new GameObject("Offset preview parent");
            var material = new Material(Shader.Find("lilToon"));
            ExportRecoveryComparisonWindow window = null;
            Texture2D image = null;
            try
            {
                parent.transform.position = new Vector3(1.2f, .2f, -.3f);
                if (transformedParent)
                {
                    parent.transform.rotation = Quaternion.Euler(0, 25, 0);
                    parent.transform.localScale = Vector3.one * 1.1f;
                }
                f.Source.transform.SetParent(parent.transform, false);
                var skins = f.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins) skin.sharedMaterial = material;
                var sourceTransforms = f.Source.GetComponentsInChildren<Transform>(true);
                var sourceMatrices = sourceTransforms.Select(t => t.localToWorldMatrix).ToArray();
                var sourceVertices = f.Mesh.vertices;
                var materialState = EditorJsonUtility.ToJson(material);
                var session = new ExportRecoverySession(f.Source, "unused.vrm", (options, report, warnings) =>
                    UniVrmOneClickExporter.Export(f.Source, "Framing regression", "Tests", warnings,
                        exporterVersion: "0.11.5", lilToonVersion: "2.3.4",
                        blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None },
                        recoveryOptions: options, recoveryReport: report));
                Assert.That(session.Attempt(null), Is.True, session.Failure?.ToString());
                window = ScriptableObject.CreateInstance<ExportRecoveryComparisonWindow>();
                Set(window, "session", session);
                await window.PrepareAsync();
                Assert.That(window.HasVrmPreview, Is.True, Get<string>(window, "error"));
                var loaded = Get<Vrm10Instance>(window, "imported");
                var render = Get<PreviewRenderUtility>(window, "vrmRender");
                var transforms = loaded.GetComponentsInChildren<Transform>(true);
                var matrices = transforms.Select(t => t.localToWorldMatrix).ToArray();
                var bounds = ExportRecoveryComparisonWindow.VisibleBounds(loaded.gameObject);
                foreach (var yaw in new[] { 0f, 45f, 90f })
                {
                    Set(window, "yaw", yaw);
                    window.RefreshPreviewFraming();
                    image = window.CapturePreview(2, 256, 256);
                    var center = render.camera.WorldToViewportPoint(bounds.center);
                    Assert.That(center.x, Is.EqualTo(.5f).Within(.001f), "yaw " + yaw);
                    Assert.That(center.y, Is.EqualTo(.5f).Within(.001f), "yaw " + yaw);
                    foreach (var corner in Corners(bounds))
                    {
                        var point = render.camera.WorldToViewportPoint(corner);
                        Assert.That(point.z, Is.GreaterThan(render.camera.nearClipPlane).And.LessThan(render.camera.farClipPlane));
                        Assert.That(point.x, Is.InRange(0f, 1f), "yaw " + yaw);
                        Assert.That(point.y, Is.InRange(0f, 1f), "yaw " + yaw);
                    }
                    Object.DestroyImmediate(image); image = null;
                }
                Assert.That(transforms.Select(t => t.localToWorldMatrix), Is.EqualTo(matrices), "Camera framing must not move the loaded model to hide export offsets.");
                Assert.That(sourceTransforms.Select(t => t.localToWorldMatrix), Is.EqualTo(sourceMatrices));
                Assert.That(f.Source.transform.parent, Is.SameAs(parent.transform));
                Assert.That(skins.All(s => s.sharedMesh == f.Mesh && s.sharedMaterial == material), Is.True);
                Assert.That(f.Mesh.vertices, Is.EqualTo(sourceVertices));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(materialState));
            }
            finally
            {
                if (image != null) Object.DestroyImmediate(image);
                if (window != null) Object.DestroyImmediate(window);
                f.Source.transform.SetParent(null, true);
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(material);
            }
        }

        static Vector3[] Corners(Bounds bounds) => (from x in new[] { -1f, 1f }
            from y in new[] { -1f, 1f }
            from z in new[] { -1f, 1f }
            select bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z))).ToArray();
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
