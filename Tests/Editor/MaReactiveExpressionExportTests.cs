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
using UnityEngine.Animations;
using UnityEngine.Playables;
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

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        public void MergeMotionActivationKeepsNonMenuReactionThroughCanonicalBuild(int pathMode, bool nestedTree)
        {
            RequirePackages();
            var mergeType = Installed(Ma + "ModularAvatarMergeBlendTree");
            if (mergeType == null) Assert.Ignore("Install the real MA Merge Motion component.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var descriptor = fixture.Source.AddComponent(Installed("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"));
            ConfigureGeometry(fixture.Mesh);
            var front = fixture.Source.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
            var back = fixture.Source.transform.Find("Back").GetComponent<SkinnedMeshRenderer>();
            front.SetBlendShapeWeight(1, 25); back.SetBlendShapeWeight(1, 25);
            var tool = new GameObject("Merge tool"); tool.transform.SetParent(fixture.Source.transform, false);
            var explicitRoot = new GameObject("Motion root"); explicitRoot.transform.SetParent(fixture.Source.transform, false);
            var basis = pathMode == 0 ? fixture.Source : pathMode == 1 ? tool : explicitRoot;
            var gate = new GameObject("Gate"); gate.transform.SetParent(basis.transform, false);
            AppearancePreparationTests.AddRule(gate, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", front.gameObject,
                ("ShapeName", "Smile"), ("ChangeType", 1), ("Value", 100f));
            gate.SetActive(false);
            // A same-named object at the wrong base must remain constant. It
            // prevents an incorrect absolute/fallback basis from passing by
            // conservatively retaining every shape changer in the hierarchy.
            var decoy = new GameObject("Gate"); decoy.transform.SetParent(pathMode == 0 ? tool.transform : fixture.Source.transform, false);
            AppearancePreparationTests.AddRule(decoy, "ModularAvatarShapeChanger", "Shapes", "ChangedShape", back.gameObject,
                ("ShapeName", "Smile"), ("ChangeType", 1), ("Value", 100f));
            decoy.SetActive(false);
            var folderName = "__MaMergeMotion_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var owned = new List<Mesh>();
            GameObject oracle = null;
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
                controller.AddParameter("Menu", AnimatorControllerParameterType.Float);
                var idle = controller.layers[0].stateMachine.AddState("Neutral"); idle.writeDefaultValues = false;
                controller.layers[0].stateMachine.defaultState = idle;
                var menu = ScriptableObject.CreateInstance(Installed("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu"));
                var parameters = ScriptableObject.CreateInstance(Installed("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
                AssetDatabase.CreateAsset(menu, folder + "/Menu.asset"); AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                ConfigureMenu(menu, parameters, AnimatorControllerParameterType.Float);
                ConfigureDescriptor(descriptor, controller, menu, parameters);
                var clip = new AnimationClip { name = "Authored non-menu activation" };
                var binding = EditorCurveBinding.FloatCurve("Gate", typeof(GameObject), "m_IsActive");
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 1));
                AssetDatabase.CreateAsset(clip, folder + "/Activation.anim");
                Motion motion = clip;
                if (nestedTree)
                {
                    var off = new AnimationClip { name = "Authored non-menu deactivation" };
                    AnimationUtility.SetEditorCurve(off, binding, AnimationCurve.Constant(0, 1, 0));
                    AssetDatabase.CreateAsset(off, folder + "/Deactivation.anim");
                    for (var i = 0; i < 2; i++)
                    {
                        var tree = new BlendTree { name = "Nested activation " + i, blendType = BlendTreeType.Simple1D,
                            blendParameter = "Menu", useAutomaticThresholds = false };
                        tree.children = i == 0 ? new[] { new ChildMotion { motion = off, threshold = 0, timeScale = 1 },
                            new ChildMotion { motion = motion, threshold = 1, timeScale = 1 } } :
                            new[] { new ChildMotion { motion = motion, threshold = 0, timeScale = 1 } };
                        AssetDatabase.CreateAsset(tree, folder + "/Tree" + i + ".asset"); motion = tree;
                    }
                }
                var merge = tool.AddComponent(mergeType);
                mergeType.GetProperty("Motion").SetValue(merge, motion);
                var mode = mergeType.GetField("PathMode"); mode.SetValue(merge, Enum.Parse(mode.FieldType, pathMode == 0 ? "Absolute" : "Relative"));
                if (pathMode == 2)
                {
                    var reference = mergeType.GetField("RelativePathRoot").GetValue(merge);
                    reference.GetType().GetMethod("Set", new[] { typeof(GameObject) }).Invoke(reference, new object[] { explicitRoot });
                    Assert.That(reference.GetType().GetMethod("Get", new[] { typeof(Component) })
                        .Invoke(reference, new object[] { fixture.Source.transform }), Is.SameAs(explicitRoot));
                }
                var settingsType = Installed(Ma + "ModularAvatarVRChatSettings");
                settingsType.GetProperty("MMDWorldSupport").SetValue(fixture.Source.AddComponent(settingsType), false);
                Assert.That(gate.GetComponentsInParent<Component>(true).All(value => value.GetType().FullName != Ma + "ModularAvatarMenuItem"), Is.True);
                var authored = fixture.Source.GetComponentsInChildren<Component>(true).Cast<Object>()
                    .Concat(AssetDatabase.FindAssets("", new[] { folder }).SelectMany(guid => AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid))))
                    .Append(fixture.Mesh).Distinct().ToArray();
                var before = authored.ToDictionary(value => value, value => EditorJsonUtility.ToJson(value));
                var expectedOff = WorldVertices(front);
                front.SetBlendShapeWeight(1, 100); var expectedOn = WorldVertices(front); front.SetBlendShapeWeight(1, 25);
                // Establish the actual official MA result without our snapshot.
                // Merge Motion's direct clip is a constant activation endpoint;
                // the nested tree supplies the real user-controlled 0/1 choice.
                oracle = Object.Instantiate(fixture.Source);
                Vector3[][] oracleVertices;
                using (NdmfExportPreparation.Prepare(fixture.Source, oracle))
                    oracleVertices = NativeMergeMotionEndpoints(oracle, nestedTree, expectedOff, expectedOn);
                Object.DestroyImmediate(fixture.Copy); fixture.Copy = Object.Instantiate(fixture.Source);
                MaAppearanceSnapshot.Apply(fixture.Source, fixture.Copy, owned);
                var path = AnimationUtility.CalculateTransformPath(gate.transform, fixture.Source.transform);
                var decoyPath = AnimationUtility.CalculateTransformPath(decoy.transform, fixture.Source.transform);
                Assert.That(fixture.Copy.transform.Find(path).GetComponentInChildren(Installed(Ma + "ModularAvatarShapeChanger"), true), Is.Not.Null);
                Assert.That(fixture.Copy.transform.Find(decoyPath).GetComponentInChildren(Installed(Ma + "ModularAvatarShapeChanger"), true), Is.Null);
                Assert.That(fixture.Copy.transform.Find("Front").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(1), Is.EqualTo(25));
                using (NdmfExportPreparation.Prepare(fixture.Source, fixture.Copy))
                {
                    var actual = NativeMergeMotionEndpoints(fixture.Copy, nestedTree, expectedOff, expectedOn);
                    for (var phase = 0; phase < actual.Length; phase++)
                        for (var i = 0; i < actual[phase].Length; i++)
                            Assert.That(Vector3.Distance(actual[phase][i], oracleVertices[phase][i]), Is.LessThan(.0005f),
                                "Snapshot preparation must preserve the official MA native endpoint.");
                }
                foreach (var value in authored) Assert.That(EditorJsonUtility.ToJson(value), Is.EqualTo(before[value]), value.name);
                Assert.That(gate.activeSelf, Is.False); Assert.That(decoy.activeSelf, Is.False);
                Assert.That(front.sharedMesh, Is.SameAs(fixture.Mesh)); Assert.That(front.GetBlendShapeWeight(1), Is.EqualTo(25));
            }
            finally { if (oracle != null) Object.DestroyImmediate(oracle); foreach (var mesh in owned) Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder); }
        }

        static Vector3[][] NativeMergeMotionEndpoints(GameObject prepared, bool selectedTree, Vector3[] expectedOff, Vector3[] expectedOn)
        {
            var runtime = VrChatExpressionMenu.Read(prepared).Controller;
            Assert.That(runtime, Is.Not.Null);
            var controller = ExpressionDependencies.Controller(runtime);
            var probe = Object.Instantiate(prepared);
            var graph = PlayableGraph.Create("MA Merge Motion independent native reference");
            try
            {
                var animator = probe.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.enabled = true; animator.applyRootMotion = false; animator.fireEvents = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, runtime);
                AnimationPlayableOutput.Create(graph, "Face", animator).SetSourcePlayable(playable);
                foreach (var parameter in controller.parameters)
                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Float: playable.SetFloat(parameter.name, parameter.defaultFloat); break;
                        case AnimatorControllerParameterType.Int: playable.SetInteger(parameter.name, parameter.defaultInt); break;
                        case AnimatorControllerParameterType.Bool: playable.SetBool(parameter.name, parameter.defaultBool); break;
                    }
                graph.Play(); graph.Evaluate(0);
                var selections = selectedTree ? new[] { 0f, 1f, 0f } : new[] { 1f };
                var result = new List<Vector3[]>();
                var skin = probe.transform.Find("Front").GetComponent<SkinnedMeshRenderer>();
                foreach (var selection in selections)
                {
                    if (selectedTree) playable.SetFloat("Menu", selection);
                    for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60f);
                    var expected = selection == 1 ? expectedOn : expectedOff;
                    Assert.That(skin.GetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex("Smile")), Is.EqualTo(selection == 1 ? 100 : 25).Within(.001f),
                        "Official MA must evaluate the authored non-menu activation endpoint " + selection);
                    var vertices = WorldVertices(skin); Assert.That(vertices.Length, Is.EqualTo(expected.Length));
                    for (var i = 0; i < vertices.Length; i++) Assert.That(Vector3.Distance(vertices[i], expected[i]), Is.LessThan(.0005f));
                    result.Add(vertices);
                }
                return result.ToArray();
            }
            finally { graph.Destroy(); Object.DestroyImmediate(probe); }
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
