using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // Exercise real MA authoring operations inside the production one-click
    // path. No test pre-applies deletion or substitutes an already-cut mesh.
    public sealed class MaDeletionExportTests
    {
        const string DeleteShape = "Removed region";
        const string BlinkShape = "Retained blink";
        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        public async Task InstalledDeletionSurvivesOneClickAndLiveMorphsRegardlessOfPreviewSettings(bool useShapeDelete, int previewMode)
        {
            var marker = Named("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
            if (marker == null || Named("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger") == null ||
                Named("nadena.dev.modular_avatar.core.ModularAvatarMeshCutter") == null)
                Assert.Ignore("Install the real Modular Avatar and NDMF packages for the deletion integration test.");
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the real one-click exporter.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            fixture.Source.AddComponent(marker);
            var mesh = Object.Instantiate(fixture.Mesh);
            var material = new Material(shader);
            var settings = ScriptableObject.CreateInstance<VRM10Object>();
            var happy = ScriptableObject.CreateInstance<VRM10Expression>();
            Vrm10Instance imported = null;
            try
            {
                mesh.name = "Two independent regions before authored deletion";
                mesh.ClearBlendShapes();
                mesh.vertices = new[] {
                    new Vector3(-.2f, 1.7f, .08f), new Vector3(-.1f, 1.7f, .08f), new Vector3(-.15f, 1.9f, .08f),
                    new Vector3(.1f, 1.7f, .08f), new Vector3(.2f, 1.7f, .08f), new Vector3(.15f, 1.9f, .08f)
                };
                mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
                mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
                mesh.uv = Enumerable.Repeat(new Vector2(.25f, .5f), 3)
                    .Concat(Enumerable.Repeat(new Vector2(.75f, .5f), 3)).ToArray();
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
                // The delete shape translates its region, without collapsing
                // it. A mere shape value can never satisfy physical deletion.
                var removedDelta = Enumerable.Repeat(Vector3.left * .04f, 3).Concat(new Vector3[3]).ToArray();
                var retainedDelta = new Vector3[3].Concat(Enumerable.Repeat(Vector3.up * .03f, 3)).ToArray();
                mesh.AddBlendShapeFrame(DeleteShape, 100, removedDelta, new Vector3[6], new Vector3[6]);
                mesh.AddBlendShapeFrame(BlinkShape, 100, retainedDelta, new Vector3[6], new Vector3[6]);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins)
                {
                    skin.sharedMesh = mesh; skin.sharedMaterial = material;
                    skin.SetBlendShapeWeight(0, 0); skin.SetBlendShapeWeight(1, 25);
                }
                var front = skins.Single(skin => skin.name == "Front");
                var back = skins.Single(skin => skin.name == "Back");
                Component rule;
                if (useShapeDelete)
                {
                    AppearancePreparationTests.AddRule(fixture.Source, "ModularAvatarShapeChanger", "Shapes", "ChangedShape",
                        front.gameObject, ("ShapeName", DeleteShape), ("ChangeType", 0), ("Value", 100f));
                    rule = fixture.Source.GetComponentInChildren(Named("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger"));
                }
                else
                {
                    var node = new GameObject("Authored cutter"); node.transform.SetParent(fixture.Source.transform, false);
                    rule = node.AddComponent(Named("nadena.dev.modular_avatar.core.ModularAvatarMeshCutter"));
                    var reference = rule.GetType().GetProperty("Object").GetValue(rule);
                    reference.GetType().GetMethod("Set", new[] { typeof(GameObject) }).Invoke(reference, new object[] { front.gameObject });
                    var filter = node.AddComponent(Named("nadena.dev.modular_avatar.core.vertex_filters.VertexFilterByAxisComponent"));
                    filter.GetType().GetProperty("Center").SetValue(filter, Vector3.zero);
                    filter.GetType().GetProperty("Axis").SetValue(filter, Vector3.left);
                }
                happy.name = "Retained endpoint";
                happy.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 1, .5f) };
                settings.Expression.Happy = happy;
                fixture.Source.AddComponent<Vrm10Instance>().Vrm = settings;
                var blink = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
                blink.Both.Add(new BlinkShapeBinding { Renderer = front, Shape = BlinkShape, Weight = 100 });

                var neutral = SourceTriangle(front, 25, true);
                var endpoint = SourceTriangle(front, 50, true);
                var closure = SourceTriangle(front, 100, true);
                var unaffected = SourceTriangle(back, 25, false);
                Assert.That(mesh.triangles.Length, Is.EqualTo(6), "The input must still contain both original triangles.");
                var beforeMesh = EditorJsonUtility.ToJson(mesh);
                var beforeRule = EditorJsonUtility.ToJson(rule);
                var beforeSettings = EditorJsonUtility.ToJson(settings);
                var beforeHappy = EditorJsonUtility.ToJson(happy);
                var sourceBefore = ExportSourceFingerprint.Compute(fixture.Source);
                using var preview = new PreviewSettingsScope(previewMode);
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Authored MA deletion", "Tests", warnings,
                    exporterVersion: "ma-deletion-regression", lilToonVersion: "2.3.4", blinkOptions: blink);
                // Check preservation before topology assertions, including a
                // failing reproduction where the copy discarded deletion.
                Assert.That(ExportSourceFingerprint.Compute(fixture.Source), Is.EqualTo(sourceBefore));
                Assert.That(EditorJsonUtility.ToJson(mesh), Is.EqualTo(beforeMesh));
                Assert.That(EditorJsonUtility.ToJson(rule), Is.EqualTo(beforeRule));
                Assert.That(EditorJsonUtility.ToJson(settings), Is.EqualTo(beforeSettings));
                Assert.That(EditorJsonUtility.ToJson(happy), Is.EqualTo(beforeHappy));
                Assert.That(front.sharedMesh, Is.SameAs(mesh)); Assert.That(back.sharedMesh, Is.SameAs(mesh));
                Assert.That(front.GetBlendShapeWeight(0), Is.Zero); Assert.That(front.GetBlendShapeWeight(1), Is.EqualTo(25));
                Assert.That(back.GetBlendShapeWeight(0), Is.Zero); Assert.That(back.GetBlendShapeWeight(1), Is.EqualTo(25));
                Assert.That(mesh.triangles.Length, Is.EqualTo(6));

                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null, string.Join("\n", warnings)); imported.Runtime.Process();
                var output = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == "Front");
                var control = imported.GetComponentsInChildren<SkinnedMeshRenderer>().Single(skin => skin.name == "Back");
                Geometry(output, neutral, "Authored deletion at exported rest");
                Geometry(control, unaffected, "Shared input must not delete the unaffected renderer");
                Assert.That(imported.Vrm.Expression.Happy, Is.Not.Null);
                imported.Runtime.Expression.SetWeight(ExpressionKey.Happy, 1); imported.Runtime.Process();
                Geometry(output, endpoint, "Retained authored endpoint after deletion");
                imported.Runtime.Expression.SetWeight(ExpressionKey.Happy, 0);
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1); imported.Runtime.Process();
                Geometry(output, closure, "Live blink must not restore removed primitives");
                // Exercise every serialized target as well: no orphan source
                // morph may make the removed side render again.
                for (var shape = 0; shape < output.sharedMesh.blendShapeCount; shape++) output.SetBlendShapeWeight(shape, 100);
                var allMorphs = BakeTriangles(output);
                Assert.That(allMorphs.Length, Is.EqualTo(3));
                Assert.That(allMorphs.All(point => point.x > .075f), Is.True,
                    "The removed negative-X region must remain absent under all serialized morphs.");
            }
            finally
            {
                if (imported != null) Object.DestroyImmediate(imported.gameObject);
                Object.DestroyImmediate(happy); Object.DestroyImmediate(settings); Object.DestroyImmediate(material); Object.DestroyImmediate(mesh);
            }
        }

        static Vector3[] SourceTriangle(SkinnedMeshRenderer skin, float blink, bool survivorOnly)
        {
            var old = skin.GetBlendShapeWeight(1);
            try
            {
                skin.SetBlendShapeWeight(1, blink);
                var points = BakeTriangles(skin);
                return survivorOnly ? points.Skip(3).ToArray() : points;
            }
            finally { skin.SetBlendShapeWeight(1, old); }
        }
        static Vector3[] BakeTriangles(SkinnedMeshRenderer skin)
        {
            var baked = new Mesh();
            try
            {
                skin.BakeMesh(baked); var vertices = baked.vertices;
                return baked.triangles.Select(index => skin.transform.TransformPoint(vertices[index])).ToArray();
            }
            finally { Object.DestroyImmediate(baked); }
        }
        static void Geometry(SkinnedMeshRenderer skin, Vector3[] expected, string message)
        {
            var actual = BakeTriangles(skin);
            Assert.That(actual.Length, Is.EqualTo(expected.Length), message + ": exactly the authored survivor triangle must remain.");
            var remaining = expected.ToList();
            foreach (var point in actual)
            {
                var index = remaining.FindIndex(value => Vector3.Distance(value, point) < .0001f);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), message + ": unexpected world vertex " + point.ToString("G9"));
                remaining.RemoveAt(index);
            }
        }
        static Type Named(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);
        static Type Required(string name) => Named(name) ?? throw new TypeLoadException(name);

        sealed class PreviewSettingsScope : IDisposable
        {
            readonly PropertyInfo ui, depth, nodeValue;
            readonly bool previousUi;
            readonly int previousDepth;
            readonly object nodePublished;
            readonly bool previousNode;
            readonly Object preferences, globalPreferences;
            readonly string preferencesJson, globalJson;
            readonly bool preferencesDirty, globalDirty;
            readonly MethodInfo plugin, validate;

            internal PreviewSettingsScope(int mode)
            {
                var preview = Required("nadena.dev.ndmf.preview.NDMFPreview");
                ui = preview.GetProperty("EnablePreviewsUI", Members); depth = preview.GetProperty("DisablePreviewDepth", Members);
                previousUi = (bool)ui.GetValue(null); previousDepth = (int)depth.GetValue(null);
                var preferenceType = Required("nadena.dev.ndmf.preview.UI.PreviewPrefs");
                preferences = (Object)preferenceType.GetProperty("instance", Members).GetValue(null);
                globalPreferences = (Object)Required("nadena.dev.ndmf.preview.NDMFPreviewPrefs").GetProperty("instance", Members).GetValue(null);
                preferencesJson = EditorJsonUtility.ToJson(preferences); globalJson = EditorJsonUtility.ToJson(globalPreferences);
                preferencesDirty = EditorUtility.IsDirty(preferences); globalDirty = EditorUtility.IsDirty(globalPreferences);
                plugin = preferenceType.GetMethod("SetPreviewPluginEnabled", Members); validate = preferenceType.GetMethod("OnValidate", Members);
                var toggle = Required("nadena.dev.modular_avatar.core.editor.MeshDeleterPreview").GetField("EnableNode", Members).GetValue(null);
                nodePublished = toggle.GetType().GetProperty("IsEnabled", Members).GetValue(toggle);
                nodeValue = nodePublished.GetType().GetProperty("Value", Members); previousNode = (bool)nodeValue.GetValue(nodePublished);
                try
                {
                    depth.SetValue(null, 0); ui.SetValue(null, mode != 1);
                    plugin.Invoke(preferences, new object[] { "nadena.dev.modular-avatar", mode != 2 });
                    nodeValue.SetValue(nodePublished, true);
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                try
                {
                    nodeValue.SetValue(nodePublished, previousNode);
                    ui.SetValue(null, previousUi); depth.SetValue(null, previousDepth);
                }
                finally
                {
                    // Restore the complete serialized preferences, including
                    // absence/order of saved node rows, plus their read caches.
                    EditorJsonUtility.FromJsonOverwrite(preferencesJson, preferences); validate.Invoke(preferences, null);
                    EditorJsonUtility.FromJsonOverwrite(globalJson, globalPreferences);
                    if (!preferencesDirty) EditorUtility.ClearDirty(preferences);
                    if (!globalDirty) EditorUtility.ClearDirty(globalPreferences);
                }
            }
        }
    }
}
