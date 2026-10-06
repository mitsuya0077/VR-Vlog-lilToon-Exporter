using System;
using System.Collections;
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
    // Real MA authoring must survive the appearance snapshot, generate FX in
    // the canonical build, and remain selectable after loading the saved VRM.
    public sealed class MaReactiveExpressionExportTests
    {
        const string Ma = "nadena.dev.modular_avatar.core.";
        static Type Installed(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);

        [Test]
        public void MixedMenuSetAndDeleteKeepsSetWithoutReapplyingDeletion()
        {
            RequirePackages();
            var source = new GameObject("Avatar");
            source.AddComponent(Installed("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot"));
            var face = new GameObject("Face"); face.transform.SetParent(source.transform, false);
            var skin = face.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh(); ConfigureGeometry(mesh); skin.sharedMesh = mesh; skin.SetBlendShapeWeight(1, 25);
            var material = new Material(Shader.Find("Hidden/VRVlogTests/lilToon")); skin.sharedMaterial = material;
            var replacement = new Material(material); replacement.SetColor("_Color", Color.red);
            var control = MenuItem(source, true);
            AppearancePreparationTests.AddRule(control.gameObject, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", face,
                ("ShapeName", "Smile"), ("ChangeType", 1), ("Value", 100f));
            var changer = control.GetComponentInChildren(Installed(Ma + "ModularAvatarShapeChanger"));
            AddShape(changer, face, "Permanent delete", "Delete", 100);
            AppearancePreparationTests.AddRule(control.gameObject, "ModularAvatarMaterialSetter", "Objects", "MaterialSwitchObject", face,
                ("Material", replacement), ("MaterialIndex", 0));
            var outfit = new GameObject("Outfit"); outfit.transform.SetParent(source.transform, false);
            AppearancePreparationTests.AddRule(control.gameObject, "ModularAvatarObjectToggle", "Objects", "ToggledObject", outfit, ("Active", false));
            var gate = new GameObject("Gate"); gate.transform.SetParent(source.transform, false); gate.SetActive(false);
            var dependent = new GameObject("Dependent face"); dependent.transform.SetParent(source.transform, false);
            var dependentSkin = dependent.AddComponent<SkinnedMeshRenderer>(); dependentSkin.sharedMesh = mesh;
            dependentSkin.SetBlendShapeWeight(1, 20); dependentSkin.sharedMaterial = material;
            AppearancePreparationTests.AddRule(gate, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", dependent,
                ("ShapeName", "Smile"), ("ChangeType", 1), ("Value", 100f));
            AppearancePreparationTests.AddRule(control.gameObject, "ModularAvatarObjectToggle", "Objects", "ToggledObject", gate, ("Active", true));
            control.transform.GetChild(control.transform.childCount - 1).name = "Dependent gate toggle";
            var clone = Object.Instantiate(source); var owned = new List<Mesh>();
            try
            {
                MaAppearanceSnapshot.Apply(source, clone, owned);
                var result = clone.transform.Find("Face").GetComponent<SkinnedMeshRenderer>();
                Assert.That(result.sharedMesh.triangles.Length, Is.EqualTo(3), "The permanent Delete must still be physically frozen.");
                Assert.That(result.GetBlendShapeWeight(1), Is.EqualTo(25), "Keep the original off endpoint even with a default-on menu.");
                var retained = clone.GetComponentInChildren(Installed(Ma + "ModularAvatarShapeChanger"));
                Assert.That(retained, Is.Not.Null);
                var shapes = (IList)retained.GetType().GetProperty("Shapes").GetValue(retained);
                Assert.That(shapes.Count, Is.EqualTo(1));
                Assert.That(shapes[0].GetType().GetField("ChangeType").GetValue(shapes[0]).ToString(), Is.EqualTo("Set"));
                Assert.That(result.sharedMaterial, Is.SameAs(material), "A dynamic material rule must reach candidate diagnostics instead of disappearing.");
                Assert.That(clone.GetComponentInChildren(Installed(Ma + "ModularAvatarMaterialSetter")), Is.Not.Null);
                Assert.That(clone.transform.Find("Outfit").gameObject.activeSelf, Is.True);
                Assert.That(clone.GetComponentInChildren(Installed(Ma + "ModularAvatarObjectToggle")), Is.Not.Null);
                Assert.That(clone.transform.Find("Gate").GetComponentInChildren(Installed(Ma + "ModularAvatarShapeChanger"), true), Is.Not.Null,
                    "An indirect menu toggle must propagate variability to its dependent Set rule.");
                Assert.That(clone.transform.Find("Dependent face").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.EqualTo(20));
                Assert.That(clone.transform.Find("Gate").gameObject.activeSelf, Is.False);
                Assert.That(((IList)changer.GetType().GetProperty("Shapes").GetValue(changer)).Count, Is.EqualTo(2));
                Assert.That(mesh.triangles.Length, Is.EqualTo(6)); Assert.That(skin.GetBlendShapeWeight(1), Is.EqualTo(25));
            }
            finally
            {
                Object.DestroyImmediate(clone); Object.DestroyImmediate(source);
                foreach (var value in owned) Object.DestroyImmediate(value);
                Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); Object.DestroyImmediate(replacement);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AuthoredActivationKeepsInactiveDependentShape(bool relativePaths)
        {
            RequirePackages();
            var source = new GameObject("Avatar");
            source.AddComponent(Installed("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot"));
            var face = new GameObject("Face"); face.transform.SetParent(source.transform, false);
            var skin = face.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh(); ConfigureGeometry(mesh); skin.sharedMesh = mesh; skin.SetBlendShapeWeight(1, 25);
            var basis = relativePaths ? new GameObject("Merge tool") : source;
            if (relativePaths) basis.transform.SetParent(source.transform, false);
            var gate = new GameObject("Gate"); gate.transform.SetParent(basis.transform, false);
            AppearancePreparationTests.AddRule(gate, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", face,
                ("ShapeName", "Smile"), ("ChangeType", 1), ("Value", 100f));
            gate.SetActive(false);
            var folderName = "__MaReactiveActivation_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            var clip = new AnimationClip { name = "Activate authored reaction" };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Gate", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.AddObjectToAsset(clip, controller); controller.layers[0].stateMachine.AddState("Selected").motion = clip;
            if (relativePaths)
            {
                var type = Installed(Ma + "ModularAvatarMergeAnimator"); var merge = basis.AddComponent(type);
                type.GetField("animator").SetValue(merge, controller);
                type.GetField("pathMode").SetValue(merge, Enum.Parse(type.GetField("pathMode").FieldType, "Relative"));
            }
            else source.AddComponent<Animator>().runtimeAnimatorController = controller;
            var clone = Object.Instantiate(source); var owned = new List<Mesh>();
            try
            {
                MaAppearanceSnapshot.Apply(source, clone, owned);
                Assert.That(clone.GetComponentsInChildren<Component>(true).Any(component => component != null &&
                    component.GetType().FullName == Ma + "ModularAvatarShapeChanger"), Is.True,
                    "An inactive authoring object may be enabled by absolute or relative animation bindings.");
                Assert.That(clone.transform.Find("Face").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.EqualTo(25));
            }
            finally
            {
                Object.DestroyImmediate(clone); Object.DestroyImmediate(source);
                foreach (var value in owned) Object.DestroyImmediate(value);
                Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder);
            }
        }

        [TestCase(false, AnimatorControllerParameterType.Int)]
        [TestCase(true, AnimatorControllerParameterType.Int)]
        [TestCase(false, AnimatorControllerParameterType.Bool)]
        [TestCase(true, AnimatorControllerParameterType.Bool)]
        [TestCase(false, AnimatorControllerParameterType.Float)]
        [TestCase(true, AnimatorControllerParameterType.Float)]
        public async Task MenuReactiveShapeSurvivesCanonicalBuildAndVrmReload(bool permanentDeletion, AnimatorControllerParameterType parameterKind)
        {
            RequirePackages();
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("Install lilToon for the real exporter entry point.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            // MA records avatar-relative reference paths when Set is called.
            // Establish the real avatar root before authoring the shape rules;
            // adding a descriptor later cannot repair a null stored path.
            var descriptor = fixture.Source.AddComponent(Installed("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"));
            ConfigureGeometry(fixture.Mesh);
            var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
            foreach (var skin in skins)
            { skin.sharedMaterial.shader = shader; skin.SetBlendShapeWeight(0, 0); skin.SetBlendShapeWeight(1, 25); }
            var control = MenuItem(fixture.Source, false);
            foreach (var skin in skins)
            {
                AppearancePreparationTests.AddRule(control.gameObject, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", skin.gameObject,
                    ("ShapeName", "Smile"), ("ChangeType", 1), ("Value", 100f));
                control.transform.GetChild(control.transform.childCount - 1).name = skin.name + " Set";
                if (permanentDeletion)
                {
                    AppearancePreparationTests.AddRule(fixture.Source, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", skin.gameObject,
                        ("ShapeName", "Permanent delete"), ("ChangeType", 0), ("Value", 100f));
                    fixture.Source.transform.GetChild(fixture.Source.transform.childCount - 1).name = skin.name + " Delete";
                }
            }
            foreach (var changer in fixture.Source.GetComponentsInChildren<Component>(true)
                .Where(component => component != null && component.GetType().FullName == Ma + "ModularAvatarShapeChanger"))
                foreach (var shape in (IList)changer.GetType().GetProperty("Shapes").GetValue(changer))
                {
                    var reference = shape.GetType().GetField("Object").GetValue(shape);
                    Assert.That(reference.GetType().GetMethod("Get", new[] { typeof(Component) }).Invoke(reference, new object[] { changer }), Is.Not.Null,
                        "The installed MA must resolve every authored fixture target before export.");
                }
            var settingsType = Installed(Ma + "ModularAvatarVRChatSettings");
            settingsType.GetProperty("MMDWorldSupport").SetValue(fixture.Source.AddComponent(settingsType), false);
            var folderName = "__MaReactiveExport_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            Vrm10Instance imported = null;
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                // MA promotes its responsive animator conditions to Float.
                // The authored SDK/FX Bool, Int and Float control metadata must
                // all retain their original numeric 0/1 selection semantics.
                controller.AddParameter("Menu", parameterKind);
                var idle = controller.layers[0].stateMachine.AddState("Neutral"); idle.writeDefaultValues = false;
                controller.layers[0].stateMachine.defaultState = idle;
                var menu = ScriptableObject.CreateInstance(Installed("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                var parameters = ScriptableObject.CreateInstance(Installed("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset"); AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                ConfigureMenu(menu, parameters, parameterKind);
                ConfigureDescriptor(descriptor, controller, menu, parameters);
                var authored = fixture.Source.GetComponentsInChildren<Component>(true).Where(value => value != null)
                    .Cast<Object>().Concat(new Object[] { fixture.Mesh, controller, menu, parameters }).ToArray();
                var before = authored.ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
                var expectedOff = skins.ToDictionary(skin => skin.name, WorldVertices);
                foreach (var skin in skins) skin.SetBlendShapeWeight(1, 100);
                var expectedOn = skins.ToDictionary(skin => skin.name, WorldVertices);
                foreach (var skin in skins) skin.SetBlendShapeWeight(1, 25);
                var warnings = new List<string>();
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "MA reactive face", "Tests", warnings,
                    blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None });
                var count = VrmMenuExpressions.CountRegistered(bytes);
                imported = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(imported, Is.Not.Null);
                const string name = "VRChat / Smile";
                var present = imported.Vrm.Expression.CustomClips.Any(clip => clip.name == name);
                CaptureEvidence(imported, name, present, count, permanentDeletion, parameterKind);
                Assert.That(count, Is.EqualTo(1), string.Join("\n", warnings));
                Assert.That(present, Is.True, "MA generated FX must remain discoverable as the authored facial menu.");
                var expression = imported.Vrm.Expression.CustomClips.Single(clip => clip.name == name);
                Assert.That(expression.IsBinary, Is.True);
                var key = ExpressionKey.CreateCustom(name);
                foreach (var weight in new[] { 0f, .49f, .5f, .51f, 1f, 0f })
                {
                    imported.Runtime.Expression.SetWeight(key, weight); imported.Runtime.Process();
                    foreach (var skin in imported.GetComponentsInChildren<SkinnedMeshRenderer>().Where(skin => expectedOff.ContainsKey(skin.name)))
                    {
                        var vertices = WorldVertices(skin); var expected = weight > .5f ? expectedOn[skin.name] : expectedOff[skin.name];
                        // Export optimization may reorder surviving vertices.
                        // Compare the visible authored geometry independently
                        // of its post-build vertex/index correspondence.
                        var remaining = (permanentDeletion ? expected.Skip(3) : expected.AsEnumerable()).ToList();
                        foreach (var index in skin.sharedMesh.triangles)
                        {
                            var match = remaining.FindIndex(point => Vector3.Distance(vertices[index], point) < .0005f);
                            Assert.That(match, Is.GreaterThanOrEqualTo(0), skin.name + " / " + weight + " / " + vertices[index]);
                            remaining.RemoveAt(match);
                        }
                        Assert.That(remaining, Is.Empty);
                        Assert.That(skin.sharedMesh.triangles.Length, Is.EqualTo(permanentDeletion ? 3 : 6));
                    }
                }
                foreach (var value in authored) Assert.That(EditorJsonUtility.ToJson(value), Is.EqualTo(before[value]), value.name);
                Assert.That(skins.All(skin => skin.sharedMesh == fixture.Mesh && skin.GetBlendShapeWeight(1) == 25), Is.True);
            }
            finally { if (imported != null) Object.DestroyImmediate(imported.gameObject); AssetDatabase.DeleteAsset(folder); }
        }

        static void RequirePackages()
        {
            foreach (var name in new[] { Ma + "ModularAvatarShapeChanger", Ma + "ModularAvatarMenuItem", Ma + "ModularAvatarMergeAnimator",
                "nadena.dev.ndmf.runtime.components.NDMFAvatarRoot", "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor",
                "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu", "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters" })
                if (Installed(name) == null) Assert.Ignore("Install the real MA, NDMF and VRChat SDK packages.");
        }

        static Component MenuItem(GameObject root, bool selected)
        {
            var node = new GameObject("Smile control"); node.transform.SetParent(root.transform, false);
            var type = Installed(Ma + "ModularAvatarMenuItem"); var component = node.AddComponent(type);
            var control = type.GetProperty("PortableControl").GetValue(component);
            var kind = control.GetType().GetProperty("Type"); kind.SetValue(control, Enum.Parse(kind.PropertyType, "Toggle"));
            control.GetType().GetProperty("Parameter").SetValue(control, "Menu");
            control.GetType().GetProperty("Value").SetValue(control, 1f);
            type.GetField("automaticValue").SetValue(component, false); type.GetField("isDefault").SetValue(component, selected);
            return component;
        }

        static void AddShape(Component changer, GameObject target, string name, string mode, float value)
        {
            var type = Installed(Ma + "ChangedShape"); var item = Activator.CreateInstance(type);
            var reference = Activator.CreateInstance(Installed(Ma + "AvatarObjectReference"));
            reference.GetType().GetMethod("Set", new[] { typeof(GameObject) }).Invoke(reference, new object[] { target });
            type.GetField("Object").SetValue(item, reference); type.GetField("ShapeName").SetValue(item, name);
            type.GetField("ChangeType").SetValue(item, Enum.Parse(type.GetField("ChangeType").FieldType, mode));
            type.GetField("Value").SetValue(item, value);
            ((IList)changer.GetType().GetProperty("Shapes").GetValue(changer)).Add(item);
        }

        static void ConfigureGeometry(Mesh mesh)
        {
            mesh.ClearBlendShapes(); mesh.triangles = Array.Empty<int>();
            mesh.vertices = new[] { new Vector3(-.03f, 1.78f, .09f), new Vector3(.03f, 1.78f, .09f), new Vector3(0, 1.83f, .09f),
                new Vector3(-.1f, 1.7f, .08f), new Vector3(.1f, 1.7f, .08f), new Vector3(0, 1.9f, .08f) };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.zero, Vector2.right, Vector2.up };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
            var delete = new Vector3[6]; for (var i = 0; i < 3; i++) delete[i] = Vector3.left * .04f;
            mesh.AddBlendShapeFrame("Permanent delete", 100, delete, null, null);
            var smile = new Vector3[6]; smile[5] = Vector3.right * .04f;
            mesh.AddBlendShapeFrame("Smile", 100, smile, null, null); mesh.RecalculateBounds();
        }

        static void ConfigureMenu(ScriptableObject menu, ScriptableObject parameters, AnimatorControllerParameterType parameterKind)
        {
            using (var data = new SerializedObject(menu))
            {
                var list = data.FindProperty("controls"); list.arraySize = 1; var item = list.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("name").stringValue = "Smile";
                var type = item.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "Toggle");
                item.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "Menu";
                item.FindPropertyRelative("value").floatValue = 1; data.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var data = new SerializedObject(parameters))
            {
                var list = data.FindProperty("parameters"); list.arraySize = 1; var item = list.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("name").stringValue = "Menu";
                var type = item.FindPropertyRelative("valueType"); type.enumValueIndex = Array.IndexOf(type.enumNames, parameterKind.ToString());
                item.FindPropertyRelative("defaultValue").floatValue = 0; data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void ConfigureDescriptor(Component descriptor, AnimatorController controller, ScriptableObject menu, ScriptableObject parameters)
        {
            using var data = new SerializedObject(descriptor);
            data.FindProperty("customExpressions").boolValue = true; data.FindProperty("expressionsMenu").objectReferenceValue = menu;
            data.FindProperty("expressionParameters").objectReferenceValue = parameters; data.FindProperty("customizeAnimationLayers").boolValue = true;
            var list = data.FindProperty("baseAnimationLayers"); list.arraySize = 1; var layer = list.GetArrayElementAtIndex(0);
            var type = layer.FindPropertyRelative("type"); type.enumValueIndex = Array.IndexOf(type.enumNames, "FX");
            layer.FindPropertyRelative("isDefault").boolValue = false; layer.FindPropertyRelative("animatorController").objectReferenceValue = controller;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        static Vector3[] WorldVertices(SkinnedMeshRenderer skin)
        {
            var baked = new Mesh();
            try { skin.BakeMesh(baked, false); return baked.vertices.Select(skin.transform.TransformPoint).ToArray(); }
            finally { Object.DestroyImmediate(baked); }
        }

        [Serializable] sealed class Evidence { public bool expressionPresent; public int registeredCount; public string expression, sourceParameterKind; public RendererEvidence[] renderers; }
        [Serializable] sealed class RendererEvidence { public string name; public Vector3[] off, on; public int[] triangles; }
        static void CaptureEvidence(Vrm10Instance imported, string name, bool present, int count, bool deletion, AnimatorControllerParameterType parameterKind)
        {
            var directory = Environment.GetEnvironmentVariable("VRVLOG_REACTIVE_EVIDENCE_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            var key = ExpressionKey.CreateCustom(name);
            if (present) imported.Runtime.Expression.SetWeight(key, 0);
            imported.Runtime.Process();
            var skins = imported.GetComponentsInChildren<SkinnedMeshRenderer>();
            var output = skins.Select(skin => new RendererEvidence { name = skin.name, off = WorldVertices(skin), triangles = skin.sharedMesh.triangles }).ToArray();
            if (present) imported.Runtime.Expression.SetWeight(key, 1);
            imported.Runtime.Process();
            for (var i = 0; i < skins.Length; i++) output[i].on = WorldVertices(skins[i]);
            if (present) imported.Runtime.Expression.SetWeight(key, 0);
            imported.Runtime.Process(); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "ma-reactive-" + parameterKind.ToString().ToLowerInvariant() + "-" + (deletion ? "with-deletion" : "plain") + ".json"),
                JsonUtility.ToJson(new Evidence { expression = name, sourceParameterKind = parameterKind.ToString(), expressionPresent = present, registeredCount = count, renderers = output }, true));
        }
    }
}
