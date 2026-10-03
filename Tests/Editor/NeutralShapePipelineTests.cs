using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class NeutralShapePipelineTests
    {
        [Serializable] public sealed class FaceEvidence
        {
            public string path;
            public Vector3[] neutralVertices, closedVertices, neutralNormals, closedNormals;
        }
        [Serializable] public sealed class FixtureEvidence
        {
            public string manualExpression = "VRChat / Authored endpoint";
            public FaceEvidence[] faces;
        }
        // This public test fixture uses artificial geometry and a generated
        // humanoid. The receiving app stores its exported bytes for regression.
        internal static byte[] ExportFixture(float endpoint = 0, string evidenceDirectory = null)
        {
            var descriptorType = Sdk("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descriptorType == null) Assert.Ignore("Install the real VRChat SDK.");
            using var f = new AttachmentConnectionTests.Fixture();
            var folder = "Assets/__NeutralPipeline_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            try
            {
                f.Mesh.ClearBlendShapes();
                Vector3[] Delta(Vector3 v) => Enumerable.Repeat(v, f.Mesh.vertexCount).ToArray();
                f.Mesh.AddBlendShapeFrame("Open", 50, Delta(Vector3.up * .02f), null, null);
                f.Mesh.AddBlendShapeFrame("Open", 100, Delta(Vector3.up * .03f), null, null);
                f.Mesh.AddBlendShapeFrame("Customization", 100, Delta(Vector3.right * .04f), null, null);
                f.Mesh.AddBlendShapeFrame("Duplicated deformation", 100, Delta(Vector3.up * .03f), null, null);
                for (var i = 3; i < 220; i++) f.Mesh.AddBlendShapeFrame("Authoring " + i, 100, Delta(Vector3.zero), null, null);
                f.Mesh.RecalculateTangents();
                var skins = f.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                for (var i = 0; i < skins.Length; i++)
                {
                    skins[i].SetBlendShapeWeight(0, 0);
                    skins[i].SetBlendShapeWeight(1, i == 0 ? 25 : 50);
                    skins[i].sharedMaterial.shader = Shader.Find("lilToon");
                }
                var vertices = f.Mesh.vertices; var normals = f.Mesh.normals; var tangents = f.Mesh.tangents;
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                controller.AddParameter("Face", AnimatorControllerParameterType.Int);
                AnimationClip Clip(string name, float weight)
                {
                    var clip = new AnimationClip { name = name };
                    foreach (var skin in skins)
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(skin.name, typeof(SkinnedMeshRenderer), "blendShape.Open"),
                            AnimationCurve.Constant(0, 1, weight));
                    AssetDatabase.AddObjectToAsset(clip, controller); return clip;
                }
                var machine = controller.layers[0].stateMachine;
                var neutral = machine.AddState("Open at startup"); neutral.writeDefaultValues = false; neutral.motion = Clip("Open at startup", 100);
                machine.defaultState = neutral;
                var selected = machine.AddState("Authored endpoint"); selected.writeDefaultValues = false; selected.motion = Clip("Authored endpoint", endpoint);
                var transition = neutral.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "Face");
                var menu = ScriptableObject.CreateInstance(Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
                using (var data = new SerializedObject(menu))
                {
                    var controls = data.FindProperty("controls"); controls.arraySize = 1;
                    var item = controls.GetArrayElementAtIndex(0); item.FindPropertyRelative("name").stringValue = "Authored endpoint";
                    var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                    item.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Face";
                    item.FindPropertyRelative("value").floatValue = 1; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var parameters = ScriptableObject.CreateInstance(Sdk("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
                AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                using (var data = new SerializedObject(parameters))
                {
                    var list = data.FindProperty("parameters"); list.arraySize = 1;
                    var item = list.GetArrayElementAtIndex(0); item.FindPropertyRelative("name").stringValue = "Face";
                    var type = item.FindPropertyRelative("valueType"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Int");
                    item.FindPropertyRelative("defaultValue").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var descriptor = f.Source.AddComponent(descriptorType);
                using (var data = new SerializedObject(descriptor))
                {
                    data.FindProperty("customExpressions").boolValue = true;
                    data.FindProperty("expressionsMenu").objectReferenceValue = menu;
                    data.FindProperty("expressionParameters").objectReferenceValue = parameters;
                    data.FindProperty("customizeAnimationLayers").boolValue = true;
                    var list = data.FindProperty("baseAnimationLayers"); list.arraySize = 1;
                    var layer = list.GetArrayElementAtIndex(0); var type = layer.FindPropertyRelative("type");
                    type.enumValueIndex = Array.IndexOf(type.enumNames, "FX"); layer.FindPropertyRelative("isDefault").boolValue = false;
                    layer.FindPropertyRelative("animatorController").objectReferenceValue = controller; data.ApplyModifiedPropertiesWithoutUndo();
                }
                var blink = new BlinkExportOptions { Mode = BlinkExportMode.Manual };
                foreach (var skin in skins) blink.Both.Add(new BlinkShapeBinding { Renderer = skin, Shape = "Open", Weight = 0 });
                var evidence = new FixtureEvidence { faces = skins.Select(skin => new FaceEvidence { path = skin.name }).ToArray() };
                if (evidenceDirectory != null)
                {
                    var measure = Object.Instantiate(f.Source);
                    try
                    {
                        foreach (var face in evidence.faces)
                        {
                            var skin = measure.transform.Find(face.path).GetComponent<SkinnedMeshRenderer>();
                            var mesh = new Mesh();
                            try
                            {
                                skin.SetBlendShapeWeight(0, 100); skin.BakeMesh(mesh);
                                face.neutralVertices = mesh.vertices; face.neutralNormals = mesh.normals;
                                skin.SetBlendShapeWeight(0, 0); skin.BakeMesh(mesh);
                                face.closedVertices = mesh.vertices; face.closedNormals = mesh.normals;
                            }
                            finally { Object.DestroyImmediate(mesh); }
                        }
                    }
                    finally { Object.DestroyImmediate(measure); }
                }
                var bytes = UniVrmOneClickExporter.Export(f.Source, "Synthetic FX neutral regression", "VR Vlog tests", blinkOptions: blink);
                Assert.That(skins.All(s => s.sharedMesh == f.Mesh && s.GetBlendShapeWeight(0) == 0), Is.True);
                Assert.That(skins.Select(s => s.GetBlendShapeWeight(1)), Is.EqualTo(new[] { 25f, 50f }));
                Assert.That(f.Mesh.vertices, Is.EqualTo(vertices)); Assert.That(f.Mesh.normals, Is.EqualTo(normals)); Assert.That(f.Mesh.tangents, Is.EqualTo(tangents));
                if (evidenceDirectory != null)
                {
                    Directory.CreateDirectory(evidenceDirectory);
                    File.WriteAllBytes(Path.Combine(evidenceDirectory, "fx-neutral.vrm"), bytes);
                    File.WriteAllText(Path.Combine(evidenceDirectory, "fx-neutral.expected.json"), JsonUtility.ToJson(evidence, true));
                }
                return bytes;
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        static Type Sdk(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

        [TestCase(0f)]
        [TestCase(25f)]
        [TestCase(-50f)]
        [TestCase(150f)]
        public async Task NeutralAndAbsoluteEndpointRoundTripWithLargeSharedMeshes(float endpoint)
        {
            var imported = await Vrm10.LoadBytesAsync(ExportFixture(endpoint), canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
            try
            {
                var expression = imported.Vrm.Expression.CustomClips.Single(c => c.name == "VRChat / Authored endpoint");
                var key = ExpressionKey.CreateCustom(expression.name);
                foreach (var input in new[] { 0f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(key, input); imported.Runtime.Process();
                    var open = input == 0 ? .03f : endpoint <= 50 ? endpoint * .0004f : .02f + (endpoint - 50) * .0002f;
                    foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        Assert.That(skin.sharedMesh.blendShapeCount, Is.GreaterThan(220));
                        var baked = new Mesh();
                        try
                        {
                            skin.BakeMesh(baked);
                            Assert.That(baked.vertices[0].y, Is.EqualTo(1.7f + open).Within(.00002));
                            Assert.That(baked.vertices[0].x, Is.EqualTo(-.1f + (skin.name == "Front" ? .01f : .02f)).Within(.00002));
                            Assert.That(Vector3.Distance(baked.normals[0], Vector3.forward), Is.LessThan(.00002));
                            Assert.That(baked.tangents.All(t => !float.IsNaN(t.x) && Mathf.Abs(t.w) == 1), Is.True);
                        }
                        finally { Object.DestroyImmediate(baked); }
                    }
                }
            }
            finally { Object.DestroyImmediate(imported.gameObject); }
        }
    }
}
