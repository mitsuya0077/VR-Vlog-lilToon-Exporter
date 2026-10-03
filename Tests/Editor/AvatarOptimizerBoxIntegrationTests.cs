using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // Reflect the installed public APIs so AAO and NDMF remain optional packages.
    // The oracle is NDMF's complete public build, independent of exporter preparation.
    public sealed class AvatarOptimizerBoxIntegrationTests
    {
        const string ConsumedMorph = "__VRVlog_Menu_box_retained";
        readonly List<Object> owned = new List<Object>();
        GameObject source;

        T Own<T>(T value) where T : Object { owned.Add(value); return value; }
        static Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);

        [TearDown]
        public void TearDown()
        {
            foreach (var value in owned.AsEnumerable().Reverse())
                if (value != null) Object.DestroyImmediate(value);
            owned.Clear();
        }

        static void RequirePackages()
        {
            if (Installed("Anatawa12.AvatarOptimizer.RemoveMeshInBox") == null || Installed("nadena.dev.ndmf.BuildContext") == null)
                Assert.Ignore("Install supported Avatar Optimizer and NDMF for the real box-removal integration tests.");
            Assert.That(ExportOptimizationMarker.AvatarOptimizerAdapterAvailable, Is.True,
                "The optional AAO integration assembly must compile and register when supported AAO is installed.");
        }

        static Component AddOptimizer(GameObject target, string name, int version)
        {
            var type = Installed("Anatawa12.AvatarOptimizer." + name);
            Assert.That(type, Is.Not.Null);
            var initialize = type.GetMethod("Initialize", new[] { typeof(int) });
            Assert.That(initialize, Is.Not.Null, "The supported AAO public configuration API is required.");
            var component = target.AddComponent(type);
            initialize.Invoke(component, new object[] { version });
            return component;
        }

        static void AddBoxes(GameObject target, bool removeInside,
            params (Vector3 Center, Vector3 Size, Quaternion Rotation)[] boxes)
        {
            var component = AddOptimizer(target, "RemoveMeshInBox", 1);
            var type = component.GetType();
            type.GetProperty("RemoveInBox").SetValue(component, removeInside);
            var boxType = type.GetNestedType("BoundingBox");
            var values = Array.CreateInstance(boxType, boxes.Length);
            for (var index = 0; index < boxes.Length; index++)
            {
                var value = Activator.CreateInstance(boxType);
                boxType.GetProperty("Center").SetValue(value, boxes[index].Center);
                boxType.GetProperty("Size").SetValue(value, boxes[index].Size);
                boxType.GetProperty("Rotation").SetValue(value, boxes[index].Rotation);
                values.SetValue(value, index);
            }
            type.GetProperty("Boxes").SetValue(component, values);
        }

        Renderer MakeRenderer(bool skinned, bool transformed, Vector3[] vertices)
        {
            source = Own(new GameObject("Box avatar"));
            var node = new GameObject("Body"); node.transform.SetParent(source.transform, false);
            if (transformed)
            {
                source.transform.SetPositionAndRotation(new Vector3(2, 3, -1), Quaternion.Euler(13, 29, 7));
                source.transform.localScale = new Vector3(1.2f, .8f, 1.4f);
                node.transform.localPosition = new Vector3(.2f, -.1f, .3f);
                node.transform.localRotation = Quaternion.Euler(-9, 15, 23);
                node.transform.localScale = new Vector3(.9f, 1.1f, .7f);
            }
            var mesh = Own(new Mesh { name = "Shared source box mesh" });
            mesh.vertices = vertices;
            mesh.triangles = Enumerable.Range(0, vertices.Length).ToArray();
            mesh.normals = Enumerable.Repeat(Vector3.forward, vertices.Length).ToArray();
            mesh.uv = vertices.Select(vertex => new Vector2(vertex.x, vertex.y)).ToArray();
            Renderer renderer;
            if (skinned)
            {
                var skin = node.AddComponent<SkinnedMeshRenderer>();
                var bone = new GameObject("Deforming bone").transform; bone.SetParent(node.transform, false);
                mesh.bindposes = new[] { bone.worldToLocalMatrix * node.transform.localToWorldMatrix };
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, vertices.Length).ToArray();
                mesh.AddBlendShapeFrame(ConsumedMorph, 100, Enumerable.Repeat(Vector3.forward * .03f, vertices.Length).ToArray(), null, null);
                skin.sharedMesh = mesh; skin.bones = new[] { bone }; skin.rootBone = bone;
                if (transformed)
                {
                    // Box classification must use the posed skin, not unskinned mesh vertices.
                    bone.localPosition = new Vector3(.35f, -.2f, 0);
                    bone.localRotation = Quaternion.Euler(0, 0, 17);
                }
                renderer = skin;
            }
            else
            {
                node.AddComponent<MeshFilter>().sharedMesh = mesh;
                renderer = node.AddComponent<MeshRenderer>();
            }
            renderer.sharedMaterial = Own(new Material(Shader.Find("Unlit/Color")) { name = "Source box material" });
            return renderer;
        }

        static Mesh MeshOf(Renderer renderer) => renderer is SkinnedMeshRenderer skin
            ? skin.sharedMesh : renderer.GetComponent<MeshFilter>().sharedMesh;

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void InsideOutsideAndPartialPrimitivesMatchCanonicalBuild(bool skinned, bool removeInside, bool transformed)
        {
            RequirePackages();
            var renderer = MakeRenderer(skinned, transformed, new[] {
                new Vector3(-.2f, -.2f, 0), new Vector3(.2f, -.2f, 0), new Vector3(0, .2f, 0), // fully inside
                new Vector3(.1f, .1f, 0), new Vector3(.2f, .2f, 0), new Vector3(1.3f, .1f, 0), // partially inside
                new Vector3(2, 0, 0), new Vector3(2.3f, 0, 0), new Vector3(2, .3f, 0) // outside
            });
            var bone = skinned ? renderer.GetComponent<SkinnedMeshRenderer>().bones[0] : null;
            AddBoxes(renderer.gameObject, removeInside,
                (bone == null ? Vector3.zero : bone.localPosition, Vector3.one,
                    bone == null ? Quaternion.identity : bone.localRotation));
            AssertMatchesCanonical(renderer, removeInside ? new[] { 1, 2 } : new[] { 0 });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RotatedBoxesClassifyEachPrimitiveAgainstTheUnion(bool skinned)
        {
            RequirePackages();
            var rotation = Quaternion.Euler(0, 0, 38);
            var points = new[] {
                rotation * new Vector3(-.32f, -.01f, 0), rotation * new Vector3(.32f, -.01f, 0), rotation * new Vector3(-.32f, .01f, 0),
                rotation * new Vector3(.8f, 0, 0), rotation * new Vector3(.85f, .02f, 0), rotation * new Vector3(.8f, .04f, 0)
            };
            var renderer = MakeRenderer(skinned, false, points);
            // No single box contains the first triangle, but their union contains all vertices.
            AddBoxes(renderer.gameObject, true,
                (rotation * new Vector3(-.32f, 0, 0), new Vector3(.16f, .12f, .2f), rotation),
                (rotation * new Vector3(.32f, 0, 0), new Vector3(.16f, .12f, .2f), rotation));
            AssertMatchesCanonical(renderer, new[] { 1 });
        }

        void AssertMatchesCanonical(Renderer renderer, int[] retainedTriangles)
        {
            var originalMesh = MeshOf(renderer);
            var vertices = originalMesh.vertices; var triangles = originalMesh.triangles;
            var originalMaterial = renderer.sharedMaterial;
            var expected = TriangleSignatures(renderer, retainedTriangles);
            var temporaryFolders = ExportFolders();
            var exportCopy = Own(Object.Instantiate(source));
            var canonicalCopy = Own(Object.Instantiate(source));
            // Canonical processing receives private inputs as well, so the reference build
            // cannot hide an exporter ownership failure by changing a shared source asset.
            foreach (var copyRenderer in canonicalCopy.GetComponentsInChildren<Renderer>())
            {
                var mesh = Own(Object.Instantiate(MeshOf(copyRenderer)));
                if (copyRenderer is SkinnedMeshRenderer skin) skin.sharedMesh = mesh;
                else copyRenderer.GetComponent<MeshFilter>().sharedMesh = mesh;
                copyRenderer.sharedMaterials = copyRenderer.sharedMaterials.Select(material => Own(Object.Instantiate(material))).ToArray();
            }
            var folder = "Assets/__AaoBoxCanonical_" + Guid.NewGuid().ToString("N");
            var guid = AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            Assert.That(guid, Is.Not.Empty);
            ExportOptimizationBindings exporterBindings = null;
            object canonicalContext = null;
            IDisposable canonicalScope = null;
            Mesh preparedMesh = null;
            try
            {
                using (var preparation = NdmfExportPreparation.Prepare(source, exportCopy,
                           afterTransforming: _ => exporterBindings = ExportOptimizationBindings.Capture(exportCopy)))
                using (var canonicalBindings = ExportOptimizationBindings.Capture(canonicalCopy))
                {
                    exporterBindings.ValidateAndApply();
                    var directory = Installed("nadena.dev.ndmf.OverrideTemporaryDirectoryScope");
                    canonicalScope = (IDisposable)Activator.CreateInstance(directory, folder);
                    var platform = Installed("nadena.dev.ndmf.platform.GenericPlatform").GetProperty("Instance").GetValue(null);
                    var provider = Installed("nadena.dev.ndmf.platform.INDMFPlatformProvider");
                    var process = Installed("nadena.dev.ndmf.AvatarProcessor").GetMethod("ProcessAvatar", new[] { typeof(GameObject), provider });
                    Assert.That(process, Is.Not.Null);
                    canonicalContext = process.Invoke(null, new[] { canonicalCopy, platform });
                    Assert.That(canonicalContext.GetType().GetProperty("Successful").GetValue(canonicalContext), Is.EqualTo(true));
                    canonicalBindings.ValidateAndApply();
                    var actual = exportCopy.GetComponentsInChildren<Renderer>().Single();
                    var canonical = canonicalCopy.GetComponentsInChildren<Renderer>().Single();
                    preparedMesh = MeshOf(actual);
                    Assert.That(TriangleSignatures(actual), Is.EquivalentTo(expected), "Only fully classified primitives may be removed.");
                    Assert.That(TriangleSignatures(actual), Is.EquivalentTo(TriangleSignatures(canonical)), "Exporter must match the full official NDMF build.");
                    Assert.That(preparedMesh.vertexCount, Is.EqualTo(expected.Length * 3), "Unused vertices must also be removed.");
                    if (renderer is SkinnedMeshRenderer)
                    {
                        var mapped = exporterBindings.MapGeneratedName(ConsumedMorph);
                        var index = preparedMesh.GetBlendShapeIndex(mapped);
                        Assert.That(index, Is.GreaterThanOrEqualTo(0));
                        Assert.That(AvatarBaseShape.HasUsableMorphEndpoint((SkinnedMeshRenderer)actual, index), Is.True);
                    }
                    Assert.That(MeshOf(renderer), Is.SameAs(originalMesh));
                    Assert.That(originalMesh.vertices, Is.EqualTo(vertices));
                    Assert.That(originalMesh.triangles, Is.EqualTo(triangles));
                    if (renderer is SkinnedMeshRenderer originalSkin)
                    {
                        Assert.That(originalMesh.GetBlendShapeName(0), Is.EqualTo(ConsumedMorph));
                        Assert.That(originalSkin.GetBlendShapeWeight(0), Is.Zero);
                        var deltas = new Vector3[vertices.Length];
                        originalMesh.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
                        Assert.That(deltas, Is.EqualTo(Enumerable.Repeat(Vector3.forward * .03f, vertices.Length).ToArray()));
                    }
                    Assert.That(renderer.sharedMaterial, Is.SameAs(originalMaterial));
                    Assert.That(renderer.GetComponent(Installed("Anatawa12.AvatarOptimizer.RemoveMeshInBox")), Is.Not.Null);
                    Assert.That(source.GetComponent<ExportOptimizationMarker>(), Is.Null);
                }
                Assert.That(preparedMesh == null, Is.True, "The exporter lease must destroy its generated mesh after use.");
            }
            finally
            {
                exporterBindings?.Dispose();
                if (canonicalContext != null && canonicalContext.GetType().GetProperty("AssetSaver").GetValue(canonicalContext) is IDisposable saver) saver.Dispose();
                canonicalScope?.Dispose();
                AssetDatabase.DeleteAsset(folder);
            }
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
            Assert.That(ExportFolders(), Is.EquivalentTo(temporaryFolders), "No exporter temporary assets may remain after disposal.");
        }

        static string[] ExportFolders() => AssetDatabase.GetSubFolders("Assets")
            .Where(path => path.StartsWith("Assets/VRVlogExportTemp-", StringComparison.Ordinal)).ToArray();

        static string[] TriangleSignatures(Renderer renderer, int[] selected = null)
        {
            Mesh baked = null;
            try
            {
                var mesh = MeshOf(renderer);
                if (renderer is SkinnedMeshRenderer skin) { baked = new Mesh(); skin.BakeMesh(baked); mesh = baked; }
                var vertices = mesh.vertices; var indices = mesh.triangles;
                string Point(int index)
                {
                    var value = renderer.transform.TransformPoint(vertices[index]);
                    return string.Join(",", new[] { value.x, value.y, value.z }.Select(coordinate =>
                        (Math.Abs(coordinate) < .00005 ? 0 : Math.Round(coordinate, 4)).ToString("F4", CultureInfo.InvariantCulture)));
                }
                return (selected ?? Enumerable.Range(0, indices.Length / 3).ToArray()).Select(triangle =>
                    string.Join("|", Enumerable.Range(triangle * 3, 3).Select(index => Point(indices[index])).OrderBy(point => point, StringComparer.Ordinal))).ToArray();
            }
            finally { if (baked != null) Object.DestroyImmediate(baked); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task BoxDeletionSurvivesExportReimportWithTraceMergeExpressionsAndMaterialRoutes(bool merge, bool fullLilToon)
        {
            RequirePackages();
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the real one-click exporter roundtrip.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var mesh = fixture.Mesh; var bindposes = mesh.bindposes;
            mesh.Clear();
            mesh.vertices = new[] {
                new Vector3(-.08f, 1.72f, .08f), new Vector3(.08f, 1.72f, .08f), new Vector3(0, 1.88f, .08f),
                new Vector3(.02f, 1.72f, .08f), new Vector3(.04f, 1.88f, .08f), new Vector3(.2f, 1.72f, .08f),
                new Vector3(.4f, 1.72f, .08f), new Vector3(.5f, 1.72f, .08f), new Vector3(.45f, 1.88f, .08f)
            };
            mesh.triangles = Enumerable.Range(0, 9).ToArray();
            mesh.normals = Enumerable.Repeat(Vector3.forward, 9).ToArray();
            mesh.uv = Enumerable.Repeat(Vector2.zero, 9).ToArray();
            mesh.bindposes = bindposes;
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 9).ToArray();
            foreach (var name in new[] { "JawOpen", "MouthClosed", "EyeClosedLeft", "EyeClosedRight" })
                mesh.AddBlendShapeFrame(name, 100, Enumerable.Repeat(Vector3.up * .03f, 9).ToArray(), null, null);
            var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
            var material = Own(new Material(shader) { name = "Box material" });
            material.SetColor("_Color", Color.blue);
            foreach (var skin in skins)
            {
                skin.sharedMaterial = material;
                for (var index = 0; index < mesh.blendShapeCount; index++) skin.SetBlendShapeWeight(index, 0);
            }
            var front = skins.Single(skin => skin.name == "Front");
            AddBoxes(front.gameObject, true, (new Vector3(0, 1.8f, .08f), new Vector3(.25f, .3f, .2f), Quaternion.identity));
            if (merge)
            {
                var target = new GameObject("Combined body"); target.transform.SetParent(fixture.Source.transform, false);
                target.AddComponent<SkinnedMeshRenderer>();
                var component = AddOptimizer(target, "MergeSkinnedMesh", 2);
                component.GetType().GetProperty("MergeBlendShapes").SetValue(component, false);
                component.GetType().GetProperty("RemoveEmptyRendererObject").SetValue(component, true);
                var set = component.GetType().GetProperty("SourceSkinnedMeshRenderers").GetValue(component);
                var add = set.GetType().GetMethod("Add", new[] { typeof(SkinnedMeshRenderer) });
                foreach (var skin in skins) add.Invoke(set, new object[] { skin });
            }
            fixture.Source.AddComponent(Installed("Anatawa12.AvatarOptimizer.TraceAndOptimize"));
            var settings = Own(ScriptableObject.CreateInstance<VRM10Object>());
            var expression = Own(ScriptableObject.CreateInstance<VRM10Expression>());
            expression.name = "Box expression";
            expression.MorphTargetBindings = new[] { new MorphTargetBinding("Front", 0, .4f) };
            expression.MaterialColorBindings = new[] { new MaterialColorBinding {
                MaterialName = material.name, BindType = MaterialColorType.color, TargetValue = Color.red } };
            settings.Expression.CustomClips.Add(expression);
            fixture.Source.AddComponent<Vrm10Instance>().Vrm = settings;
            var originalVertices = mesh.vertices; var originalTriangles = mesh.triangles;
            var temporaryFolders = ExportFolders();
            Vrm10Instance imported = null;
            try
            {
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Box regression", "Tests", warnings,
                    exporterVersion: fullLilToon ? "box-regression" : null, lilToonVersion: fullLilToon ? "2.3.4" : null);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null, string.Join("\n", warnings));
                var importedSkins = imported.GetComponentsInChildren<SkinnedMeshRenderer>();
                Assert.That(importedSkins.Sum(skin => skin.sharedMesh.triangles.Length / 3), Is.EqualTo(5),
                    "Only the fully enclosed Front triangle is removed; Back and the partial triangle survive.");
                if (merge) Assert.That(importedSkins.Length, Is.EqualTo(1), "Explicit AAO merge must run after box removal.");
                var mapped = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == expression.name);
                Assert.That(mapped.MorphTargetBindings.Length, Is.EqualTo(1));
                Assert.That(mapped.MaterialColorBindings.Length, Is.EqualTo(1));
                Assert.That(mapped.MaterialColorBindings[0].TargetValue, Is.EqualTo((Vector4)Color.red));
                Assert.That(importedSkins.SelectMany(skin => skin.sharedMaterials).Any(value =>
                    value.name == mapped.MaterialColorBindings[0].MaterialName), Is.True,
                    "The optimized material binding must resolve to an actual exported material.");
                foreach (var name in new[] { "UE/JawOpen", "UE/MouthClosed" })
                {
                    var tracking = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == name);
                    Assert.That(tracking.MorphTargetBindings, Is.Not.Empty);
                    foreach (var binding in tracking.MorphTargetBindings)
                    {
                        var skin = imported.transform.Find(binding.RelativePath).GetComponent<SkinnedMeshRenderer>();
                        Assert.That(binding.Index, Is.InRange(0, skin.sharedMesh.blendShapeCount - 1));
                        Assert.That(AvatarBaseShape.HasUsableMorphEndpoint(skin, binding.Index), Is.True);
                    }
                }
                var morph = mapped.MorphTargetBindings[0];
                var mappedSkin = imported.transform.Find(morph.RelativePath).GetComponent<SkinnedMeshRenderer>();
                foreach (var weight in new[] { 0f, .5f, 1f })
                {
                    imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.name), weight);
                    imported.Runtime.Process();
                    Assert.That(mappedSkin.GetBlendShapeWeight(morph.Index), Is.EqualTo(40 * weight).Within(.001));
                    Assert.That(importedSkins.Sum(skin => skin.sharedMesh.triangles.Length / 3), Is.EqualTo(5));
                }
                Assert.That(imported.Vrm.Expression.Blink.MorphTargetBindings, Is.Not.Empty);
                imported.Runtime.Expression.SetWeight(ExpressionKey.CreateCustom(expression.name), 0);
                imported.Runtime.Expression.SetWeight(ExpressionKey.Blink, 1);
                imported.Runtime.Process();
                Assert.That(importedSkins.Sum(skin => skin.sharedMesh.triangles.Length / 3), Is.EqualTo(5));
                Assert.That(mesh.vertices, Is.EqualTo(originalVertices));
                Assert.That(mesh.triangles, Is.EqualTo(originalTriangles));
                Assert.That(front.sharedMesh, Is.SameAs(mesh));
                Assert.That(material.GetColor("_Color"), Is.EqualTo(Color.blue));
                Assert.That(expression.MorphTargetBindings[0].RelativePath, Is.EqualTo("Front"));
                Assert.That(expression.MaterialColorBindings[0].MaterialName, Is.EqualTo(material.name));
                Assert.That(fixture.Source.GetComponent<Vrm10Instance>().Vrm, Is.SameAs(settings));
                Assert.That(fixture.Source.GetComponent<ExportOptimizationMarker>(), Is.Null);
                Assert.That(ExportFolders(), Is.EquivalentTo(temporaryFolders));
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); }
        }
    }
}
