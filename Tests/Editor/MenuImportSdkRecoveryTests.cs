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
using VRVlog.Poses;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    // Reflection keeps the SDK optional. These tests use its real serialized
    // descriptor/menu types when installed and explicitly skip otherwise.
    public sealed class MenuImportSdkRecoveryTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task OversizedMenuCanRetrySaveAndReloadWithoutChangingSharedInputs(bool omitAll)
        {
            using var f = new AttachmentConnectionTests.Fixture();
            using var menus = new SdkFixture(f.Source);
            var material = new Material(Shader.Find("lilToon")) { name = "Shared release-recovery material" };
            var settings = ScriptableObject.CreateInstance<VRM10Object>();
            var happy = ScriptableObject.CreateInstance<VRM10Expression>();
            var manual = HumanoidPoseTests.Clip(f.Source);
            var directory = Path.Combine(Path.GetTempPath(), "vrvlog-menu-recovery-" + Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(directory, "existing.vrm");
            Vrm10Instance loaded = null;
            try
            {
                happy.MorphTargetBindings = new[] { new MorphTargetBinding { RelativePath = "Front", Index = 0, Weight = .4f } };
                settings.Expression.Happy = happy;
                f.Source.AddComponent<Vrm10Instance>().Vrm = settings;
                var skins = f.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var skin in skins) skin.sharedMaterial = material;
                var beforeMesh = EditorJsonUtility.ToJson(f.Mesh);
                var beforeMaterial = EditorJsonUtility.ToJson(material);
                var beforeSettings = EditorJsonUtility.ToJson(settings);
                var beforeHappy = EditorJsonUtility.ToJson(happy);
                var beforeDescriptor = EditorJsonUtility.ToJson(menus.Descriptor);
                var beforeMenus = menus.MenuJson();
                var transforms = f.Source.GetComponentsInChildren<Transform>(true);
                var originalTransforms = transforms.Select(t => EditorJsonUtility.ToJson(t)).ToArray();
                var poseOptions = new PoseExportOptions(); poseOptions.Manual.Add(new ManualPose { Clip = manual, Name = "Manual survives menu omission" });
                Directory.CreateDirectory(directory); var existing = new byte[] { 61, 62, 63 }; File.WriteAllBytes(destination, existing);
                var session = new ExportRecoverySession(f.Source, destination, (options, report, warnings) =>
                    UniVrmOneClickExporter.Export(f.Source, "Menu recovery integration", "Tests", warnings,
                        exporterVersion: "menu-recovery-test", lilToonVersion: "2.3.4",
                        gimmickOptions: new ExportGimmickOptions { AutoExclude = false },
                        blinkOptions: new BlinkExportOptions { Mode = BlinkExportMode.None }, poseOptions: poseOptions,
                        recoveryOptions: options, recoveryReport: report));

                Assert.That(session.Attempt(null), Is.False);
                Assert.That(session.Failure, Is.TypeOf<VrChatMenuImportLimitException>());
                Assert.That(((VrChatMenuImportLimitException)session.Failure).Summary.CandidateCount, Is.EqualTo(257));
                var options = new ExportRecoveryOptions();
                var chosen = session.AvailableDiagnostics.Single(d => omitAll
                    ? d.Action.Kind == ExportRecoveryActionKind.SkipVrChatMenus
                    : d.Action.Kind == ExportRecoveryActionKind.ExcludeMenuBranch && d.Action.MenuPath == "/1");
                Assert.That(chosen.Reason, Is.Not.Empty); Assert.That(chosen.LostEffect, Is.Not.Empty);
                options.Actions.Add(chosen.Action);
                Assert.That(session.Attempt(options), Is.True, session.Failure?.ToString());
                Assert.That(session.CanSave, Is.True);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing), "A successful retry is still a draft until confirmation.");
                var draft = session.LastSuccess;
                Assert.That(draft.Options.Actions.Single().Kind, Is.EqualTo(chosen.Action.Kind));
                Assert.DoesNotThrow(() => LilToonGlbExtension.Validate(draft.Bytes));
                loaded = await Vrm10.LoadBytesAsync(draft.Bytes, canLoadVrm0X: false, awaitCaller: new ImmediateCaller());
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.Vrm.Expression.CustomClips.Any(c => c.name.Contains("Menu face")), Is.EqualTo(!omitAll),
                    "Loaded expressions: " + string.Join(", ", loaded.Vrm.Expression.CustomClips.Select(c => c.name)) + "\n" + string.Join("\n", draft.Warnings));
                Assert.That(loaded.Vrm.Expression.CustomClips.Any(c => c.name.Contains("Gesture face")), Is.True,
                    "The menu policy must not disable FX gesture clip discovery. Loaded expressions: " +
                    string.Join(", ", loaded.Vrm.Expression.CustomClips.Select(c => c.name)) + "\n" + string.Join("\n", draft.Warnings));
                Assert.That(loaded.Vrm.Expression.Happy, Is.Not.Null, "Existing VRM expression settings remain part of the export.");
                Assert.That(loaded.GetComponentsInChildren<SkinnedMeshRenderer>().Select(s => s.name), Is.EquivalentTo(new[] { "Front", "Back" }));
                Assert.That(loaded.GetComponentsInChildren<SkinnedMeshRenderer>().All(s => s.sharedMesh.vertexCount == 3 && s.bones.All(b => b != null)), Is.True);
                var extensions = (Dictionary<string, object>)GlbDocument.Read(draft.Bytes).Json["extensions"];
                Assert.That(HumanoidPoseData.Read(extensions[HumanoidPoseData.Extension]).Any(p => p.Name == "Manual survives menu omission"), Is.True,
                    "The same omission policy reaches prepared pose-menu re-reading while retaining manual poses.");

                // Removing consent reruns the full original menu and fails. It
                // may not replace either a draft or the existing destination.
                options.Actions.Clear();
                Assert.That(draft.Options.Actions.Count, Is.EqualTo(1), "The successful result captures consent independently of a later UI selection edit.");
                Assert.That(session.Attempt(options), Is.False);
                Assert.That(session.Failure, Is.TypeOf<VrChatMenuImportLimitException>());
                Assert.That(session.LastSuccess, Is.SameAs(draft));
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(existing));
                options.Actions.Add(chosen.Action);
                Assert.That(session.Attempt(options), Is.True, session.Failure?.ToString());
                session.SavePending();
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(session.LastSuccess.Bytes));
                Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { destination }));
                Assert.That(EditorJsonUtility.ToJson(menus.Descriptor), Is.EqualTo(beforeDescriptor));
                Assert.That(menus.MenuJson(), Is.EqualTo(beforeMenus));
                Assert.That(EditorJsonUtility.ToJson(f.Mesh), Is.EqualTo(beforeMesh));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(beforeMaterial));
                Assert.That(EditorJsonUtility.ToJson(settings), Is.EqualTo(beforeSettings));
                Assert.That(EditorJsonUtility.ToJson(happy), Is.EqualTo(beforeHappy));
                Assert.That(transforms.Select(t => EditorJsonUtility.ToJson(t)), Is.EqualTo(originalTransforms));
                Assert.That(skins.All(s => s.sharedMesh == f.Mesh && s.sharedMaterial == material && s.GetBlendShapeWeight(0) == 35), Is.True);
            }
            finally
            {
                if (loaded != null) Object.DestroyImmediate(loaded.gameObject);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Object.DestroyImmediate(manual); Object.DestroyImmediate(happy); Object.DestroyImmediate(settings); Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ForeignOwnerAndReplacedRootCannotReuseARecoveredBranchRecipe()
        {
            using var f = new AttachmentConnectionTests.Fixture(); using var menus = new SdkFixture(f.Source);
            var error = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.Read(f.Source));
            var report = ExportRecoveryReport.FromException(f.Source, error);
            var branch = report.Diagnostics.Single(d => d.Action?.Kind == ExportRecoveryActionKind.ExcludeMenuBranch && d.Action.MenuPath == "/1").Action;
            var options = new ExportRecoveryOptions(); options.Actions.Add(branch);
            Assert.That(ExportRecoveryReport.MenuImportPolicy(f.Source, options).ExcludedBranches, Does.Contain("/1"));
            var foreign = new GameObject("Foreign avatar");
            try
            {
                Assert.Throws<InvalidOperationException>(() => ExportRecoveryReport.MenuImportPolicy(foreign, options));
                var original = branch.MenuOwner; branch.MenuOwner = foreign;
                Assert.Throws<InvalidOperationException>(() => ExportRecoveryReport.MenuImportPolicy(f.Source, options));
                branch.MenuOwner = original;
                SdkFixture.Set(menus.Descriptor, "expressionsMenu", menus.NewMenu());
                Assert.Throws<InvalidOperationException>(() => ExportRecoveryReport.MenuImportPolicy(f.Source, options));
            }
            finally { Object.DestroyImmediate(foreign); }
        }

        [Test]
        public void MenuAssetEditsInvalidateSelectionAndPendingSaveUntilFullReinspection()
        {
            using var f = new AttachmentConnectionTests.Fixture(); using var menus = new SdkFixture(f.Source);
            var session = new ExportRecoverySession(f.Source, "unused.vrm", (options, report, warnings) =>
            {
                var policy = ExportRecoveryReport.MenuImportPolicy(f.Source, options);
                VrChatExpressionMenu.Read(f.Source, policy);
                return new byte[] { 1 };
            });
            Assert.That(session.Attempt(null), Is.False);
            var options = new ExportRecoveryOptions(); options.Actions.Add(session.AvailableDiagnostics.Single(d => d.Action.Kind == ExportRecoveryActionKind.SkipVrChatMenus).Action);
            Assert.That(session.Attempt(options), Is.True);
            var last = session.LastSuccess;
            SdkFixture.Set(((IList)VrChatExpressionMenu.Member(menus.Root, "controls"))[0], "name", "Changed original label");
            EditorUtility.SetDirty(menus.Root);
            Assert.That(session.CheckForChanges(), Is.True);
            Assert.That(session.SelectedOptions.Actions, Is.Empty);
            Assert.That(session.AvailableDiagnostics, Is.Empty);
            Assert.That(session.CanSave, Is.False);
            Assert.That(session.Attempt(options), Is.False);
            Assert.That(session.LastSuccess, Is.SameAs(last));
            Assert.That(session.Reinspect(), Is.False, "Reinspection requires new explicit consent to omit the changed menu.");
            Assert.That(session.SelectedOptions.Actions, Is.Empty);
            Assert.That(session.Failure, Is.TypeOf<VrChatMenuImportLimitException>());
        }

        [Test]
        public void PreparedPoseMenuRereadUsesScopeAndRejectsReplacedBranchIdentity()
        {
            using var f = new AttachmentConnectionTests.Fixture(); using var menus = new SdkFixture(f.Source);
            var error = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.Read(f.Source));
            var options = new ExportRecoveryOptions();
            options.Actions.Add(ExportRecoveryReport.FromException(f.Source, error).Diagnostics.Single(d =>
                d.Action?.Kind == ExportRecoveryActionKind.ExcludeMenuBranch && d.Action.MenuPath == "/1").Action);
            var policy = ExportRecoveryReport.MenuImportPolicy(f.Source, options);
            var clone = Object.Instantiate(f.Source);
            try
            {
                Assert.Throws<VrChatMenuImportLimitException>(() => PoseMenuResolver.Read(clone), "The unscoped prepared read still exposes the original failure.");
                Assert.That(PoseMenuResolver.Read(clone, policy).Count, Is.EqualTo(1));
                var descriptor = clone.GetComponents<Component>().Single(c => c.GetType() == menus.Descriptor.GetType());
                SdkFixture.Set(descriptor, "expressionsMenu", menus.NewMenu());
                Assert.Throws<VrChatMenuImportPolicyException>(() => PoseMenuResolver.Read(clone, policy),
                    "Preparation cannot map a scope onto a new menu solely because its index path looks the same.");
                policy.SkipAll = true;
                Assert.That(PoseMenuResolver.Read(clone, policy), Is.Empty);
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void WholeMenuOmissionKeepsFaceEmoRegisteredClipParsingIndependent()
        {
            using var f = new AttachmentConnectionTests.Fixture(); using var menus = new SdkFixture(f.Source);
            var policy = new VrChatMenuImportPolicy { ExpectedRoot = menus.Root, SkipAll = true };
            var source = VrChatExpressionMenu.Read(f.Source, policy);
            Assert.That(source.Entries, Is.Empty);
            var folderName = "__MenuRecoveryFaceEmo_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName); var folder = "Assets/" + folderName;
            var clip = menus.FaceClip("FaceEmo survives", 62, persist: false);
            try
            {
                AssetDatabase.CreateAsset(clip, folder + "/Registered.anim");
                var registered = new { Modes = new[] { new { DisplayName = "Registered", Branches = new[] { new { BaseAnimation = new { GUID = AssetDatabase.AssetPathToGUID(folder + "/Registered.anim") } } } } } };
                var serial = 0;
                FaceEmoExpressions.ReadRegistered(f.Source, registered, "FaceEmo", source, ref serial, new HashSet<object>(), 0);
                Assert.That(source.Entries.Single().Name, Does.StartWith("FaceEmo / Registered"));
                Assert.That(source.Entries.Single().Error, Is.Null);
                Assert.That(source.Entries.Single().Values.Single().Weight, Is.EqualTo(62));
                Assert.That(VrChatExpressionMenu.Read(f.Source, policy).Entries, Is.Empty);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void WholeMenuOmissionRetainsRealAplAndManualPoseRegistration()
        {
            var aplType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(PoseExportSession.AplType)).FirstOrDefault(t => t != null);
            if (aplType == null) Assert.Ignore("Install the real Avatar Pose Library package to verify its serialized registration together with menu omission.");
            using var f = new AttachmentConnectionTests.Fixture(); using var menus = new SdkFixture(f.Source);
            var child = new GameObject("APL registration"); child.transform.SetParent(f.Source.transform, false);
            var apl = child.AddComponent(aplType); var clip = HumanoidPoseTests.Clip(f.Source); GameObject clone = null;
            try
            {
                var data = Activator.CreateInstance(aplType.GetField("data").FieldType); SdkFixture.Set(apl, "data", data);
                var categories = (IList)PoseMenuResolver.Member(data, "categories");
                var category = Activator.CreateInstance(categories.GetType().GetGenericArguments()[0]); categories.Add(category); SdkFixture.Set(category, "name", "APL category");
                var poses = (IList)PoseMenuResolver.Member(category, "poses");
                var pose = Activator.CreateInstance(poses.GetType().GetGenericArguments()[0]); poses.Add(pose);
                SdkFixture.Set(pose, "name", "APL survives"); SdkFixture.Set(pose, "animationClip", clip);
                var before = EditorJsonUtility.ToJson(apl);
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Name = "Manual survives", Clip = clip });
                var policy = new VrChatMenuImportPolicy { SkipAll = true, ExpectedRoot = menus.Root };
                using (var snapshot = new PoseExportSession(f.Source, options, menuPolicy: policy))
                {
                    clone = Object.Instantiate(f.Source); PoseExportSession.RemoveAplFromCopy(f.Source, clone);
                    Assert.That(clone.GetComponentInChildren(aplType), Is.Null);
                    snapshot.CollectPrepared(clone);
                    Assert.That(snapshot.Entries.Single(e => e.Error == null).Source, Does.Contain("APL").And.Contain("手動"));
                    Assert.That(snapshot.Entries.Single(e => e.Error == null).Data, Is.Not.Null);
                }
                Assert.That(EditorJsonUtility.ToJson(apl), Is.EqualTo(before));
                Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.Read(f.Source));
            }
            finally { Object.DestroyImmediate(clone); Object.DestroyImmediate(clip); }
        }

        [Test]
        public void SharedMenuLimitDiagnosticOmitsPrivateNamesAndLocalRoutes()
        {
            using var f = new AttachmentConnectionTests.Fixture(); using var menus = new SdkFixture(f.Source);
            f.Source.name = "PRIVATE_AVATAR_731"; menus.Root.name = "PRIVATE_MENU_846";
            SdkFixture.Set(((IList)VrChatExpressionMenu.Member(menus.Root, "controls"))[1], "name", "PRIVATE_BRANCH_923");
            var error = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.Read(f.Source));
            var report = ExportRecoveryReport.FromException(f.Source, error, "表情メニュー読込");
            Assert.That(report.Diagnostics.Any(d => d.Target.Contains("PRIVATE_BRANCH_923")), Is.True,
                "Private labels remain visible locally to identify the selected branch.");
            var shared = report.BuildSupportText();
            Assert.That(shared, Does.Contain("Stage: expression-menu"));
            Assert.That(shared, Does.Contain("menu-import-limit"));
            Assert.That(shared, Does.Not.Contain("PRIVATE_"));
            Assert.That(shared, Does.Not.Contain("/1"));
            Assert.That(shared, Does.Not.Contain(f.Source.GetInstanceID().ToString()));
        }

        [Test]
        public void ChangedPreparedMenuDropsOnlyBranchCandidatesAndRetainsTheSuccessfulScopeLabel()
        {
            using var f = new AttachmentConnectionTests.Fixture(); using var menus = new SdkFixture(f.Source);
            var material = f.Source.GetComponentInChildren<Renderer>().sharedMaterial;
            material.shader = Shader.Find("lilToon"); material.SetFloat("_UseAudioLink", 1);
            var initialError = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.Read(f.Source));
            var audioError = ExportRecoveryFailure.At(new NotSupportedException("AudioLink unsupported"), "audio-link", material);
            var phase = 0;
            var session = new ExportRecoverySession(f.Source, "unused.vrm", (options, report, warnings) =>
            {
                if (phase == 0)
                {
                    report.Fail(f.Source, initialError);
                    report.Diagnostics.AddRange(ExportRecoveryReport.FromException(f.Source, audioError).Diagnostics);
                    throw initialError;
                }
                if (phase == 2) throw new VrChatMenuImportPolicyException();
                VrChatExpressionMenu.Read(f.Source, ExportRecoveryReport.MenuImportPolicy(f.Source, options));
                return new byte[] { 81, 82, 83 };
            });
            Assert.That(session.Attempt(null), Is.False);
            var scope = session.AvailableDiagnostics.Single(d => d.Action.Kind == ExportRecoveryActionKind.ExcludeMenuBranch && d.Action.MenuPath == "/1");
            var audio = session.AvailableDiagnostics.Single(d => d.Action.Kind == ExportRecoveryActionKind.DisableAudioLink);
            var options = new ExportRecoveryOptions(); options.Actions.Add(scope.Action); options.Actions.Add(audio.Action);
            phase = 1; Assert.That(session.Attempt(options), Is.True);
            var successful = session.LastSuccess; var label = scope.Target; var effect = scope.LostEffect;
            phase = 2; Assert.That(session.Attempt(options), Is.False);
            Assert.That(session.AvailableDiagnostics.Any(d => d.Action.Kind == ExportRecoveryActionKind.ExcludeMenuBranch), Is.False);
            Assert.That(session.AvailableDiagnostics.Any(d => d.Action.Kind == ExportRecoveryActionKind.DisableAudioLink), Is.True);
            Assert.That(session.AvailableDiagnostics.Any(d => d.Action.Kind == ExportRecoveryActionKind.SkipVrChatMenus), Is.True);
            Assert.That(session.LastSuccess, Is.SameAs(successful));
            Assert.That(session.LastSuccess.Bytes, Is.EqualTo(new byte[] { 81, 82, 83 }));
            scope.Target = "Later diagnostic label"; scope.LostEffect = "Later diagnostic effect";
            var captured = successful.Diagnostics.Single(d => d.Action.Kind == ExportRecoveryActionKind.ExcludeMenuBranch);
            Assert.That(captured.Target, Is.EqualTo(label)); Assert.That(captured.LostEffect, Is.EqualTo(effect));
            Assert.That(captured.Action.MenuPath, Is.EqualTo("/1"));
        }

        internal sealed class SdkFixture : IDisposable
        {
            internal readonly Component Descriptor;
            internal readonly ScriptableObject Root;
            readonly List<Object> owned = new List<Object>();
            readonly Type menuType;
            readonly string directory;
            int menuSerial, clipSerial;
            internal SdkFixture(GameObject avatar)
            {
                var descriptorType = Find("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
                menuType = Find("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu");
                var parameterType = Find("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters");
                if (descriptorType == null || menuType == null || parameterType == null)
                    Assert.Ignore("Install the real VRChat SDK to run descriptor/menu retry, prepared pose reread and full VRM round-trip tests.");
                var folderName = "__MenuRecoverySdk_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", folderName); directory = "Assets/" + folderName;
                Descriptor = avatar.AddComponent(descriptorType); Set(Descriptor, "customExpressions", true); Set(Descriptor, "customizeAnimationLayers", true);
                Root = NewMenu(); var face = NewMenu(); AddControl(face, "Menu face", "Toggle", "Face", 1);
                AddControl(Root, "Faces", "SubMenu", subMenu: face);
                AddControl(Root, "Clothes", "SubMenu", subMenu: EightControlTree(256));
                Set(Descriptor, "expressionsMenu", Root);
                var parameters = ScriptableObject.CreateInstance(parameterType); owned.Add(parameters);
                AssetDatabase.CreateAsset(parameters, directory + "/Parameters.asset");
                var parameterField = parameterType.GetField("parameters"); var rowType = parameterField.FieldType.GetElementType();
                var rows = Array.CreateInstance(rowType, 1); var row = Activator.CreateInstance(rowType);
                Set(row, "name", "Face"); Set(row, "valueType", "Int"); Set(row, "defaultValue", 0f); rows.SetValue(row, 0);
                parameterField.SetValue(parameters, rows); Set(Descriptor, "expressionParameters", parameters);
                // Unity creates and owns the serialized native Animator graph.
                // A bag of newly allocated state machines with empty names is
                // not equivalent to an authored FX controller's state paths.
                var fx = AnimatorController.CreateAnimatorControllerAtPath(directory + "/FX.controller"); owned.Add(fx);
                fx.AddParameter("Face", AnimatorControllerParameterType.Int); fx.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
                var faceMachine = fx.layers[0].stateMachine; faceMachine.name = "Menu face";
                var idle = faceMachine.AddState("Idle"); var smile = faceMachine.AddState("Smile");
                idle.writeDefaultValues = smile.writeDefaultValues = false;
                idle.motion = FaceClip("Idle face", 35); smile.motion = FaceClip("Selected face", 75);
                faceMachine.defaultState = idle;
                Transition(idle, smile, "Face", 1); Transition(smile, idle, "Face", 0);
                fx.AddLayer("Gesture face"); var gestureMachine = fx.layers[1].stateMachine;
                var neutral = gestureMachine.AddState("Neutral"); neutral.writeDefaultValues = false;
                var gesture = gestureMachine.AddState("Gesture face"); gesture.writeDefaultValues = false; gesture.motion = FaceClip("Gesture face", 85);
                gestureMachine.defaultState = neutral; Transition(neutral, gesture, "GestureRight", 2);
                var layers = fx.layers; layers[0].name = "Menu face"; layers[0].defaultWeight = 1; layers[1].defaultWeight = 1;
                fx.layers = layers;
                Layers(Descriptor, "baseAnimationLayers", new[] { "Base", "Additive", "Gesture", "Action", "FX" }, fx);
                Layers(Descriptor, "specialAnimationLayers", new[] { "Sitting", "TPose", "IKPose" }, fx);
                // Save only assets owned by this fixture; never flush unrelated
                // dirty assets or the user's scene during a test.
                foreach (var asset in owned.Where(EditorUtility.IsPersistent))
                { EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset); }
            }
            static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            internal static void Set(object target, string name, object value)
            {
                var field = target.GetType().GetField(name); Assert.That(field, Is.Not.Null, name);
                field.SetValue(target, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value.ToString()) : value);
            }
            internal ScriptableObject NewMenu()
            {
                var menu = ScriptableObject.CreateInstance(menuType); owned.Add(menu);
                AssetDatabase.CreateAsset(menu, directory + "/Menu" + menuSerial++ + ".asset"); return menu;
            }
            internal AnimationClip FaceClip(string name, float weight, bool persist = true)
            {
                var clip = new AnimationClip { name = name }; owned.Add(clip);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Front", typeof(SkinnedMeshRenderer), "blendShape.Hair detail"), AnimationCurve.Constant(0, 1, weight));
                // Native asset creation/reload can take its display name from
                // the file name. Preserve the fixture's authored clip label so
                // gesture discovery is checked against that actual identity.
                if (persist) AssetDatabase.CreateAsset(clip, directory + "/" + name + "-" + clipSerial++ + ".anim");
                return clip;
            }
            ScriptableObject EightControlTree(int count)
            {
                var menu = NewMenu();
                if (count <= 8) { for (var i = 0; i < count; i++) AddControl(menu, "Wardrobe " + i, "RadialPuppet"); return menu; }
                var remaining = count;
                while (remaining > 0)
                {
                    var group = Math.Min(count <= 64 ? 8 : 64, remaining);
                    AddControl(menu, "Wardrobe page " + ((IList)VrChatExpressionMenu.Member(menu, "controls")).Count, "SubMenu", subMenu: EightControlTree(group));
                    remaining -= group;
                }
                return menu;
            }
            static void AddControl(ScriptableObject menu, string name, string type, string parameterName = null, float value = 0, ScriptableObject subMenu = null)
            {
                var controls = (IList)VrChatExpressionMenu.Member(menu, "controls");
                var control = Activator.CreateInstance(controls.GetType().GetGenericArguments()[0]); controls.Add(control);
                Set(control, "name", name); Set(control, "type", type); Set(control, "value", value); Set(control, "subMenu", subMenu);
                if (parameterName != null)
                {
                    var parameter = Activator.CreateInstance(control.GetType().GetField("parameter").FieldType);
                    Set(parameter, "name", parameterName); Set(control, "parameter", parameter);
                }
                EditorUtility.SetDirty(menu);
            }
            void Layers(Component descriptor, string fieldName, string[] types, AnimatorController fx)
            {
                var field = descriptor.GetType().GetField(fieldName); var rowType = field.FieldType.GetElementType(); var rows = Array.CreateInstance(rowType, types.Length);
                for (var i = 0; i < types.Length; i++)
                {
                    var row = Activator.CreateInstance(rowType); Set(row, "type", types[i]); Set(row, "isDefault", false);
                    if (types[i] == "FX") Set(row, "animatorController", fx);
                    else { var empty = new AnimatorController(); owned.Add(empty); Set(row, "animatorController", empty); }
                    rows.SetValue(row, i);
                }
                field.SetValue(descriptor, rows);
            }
            static void Transition(AnimatorState from, AnimatorState to, string parameter, int value)
            { var transition = from.AddTransition(to); transition.hasExitTime = false; transition.duration = 0; transition.AddCondition(AnimatorConditionMode.Equals, value, parameter); }
            internal string[] MenuJson() => owned.OfType<ScriptableObject>().Where(o => o.GetType() == menuType).Select(o => EditorJsonUtility.ToJson(o)).ToArray();
            public void Dispose()
            {
                if (!string.IsNullOrEmpty(directory)) AssetDatabase.DeleteAsset(directory);
                foreach (var value in owned.Where(v => v != null && !EditorUtility.IsPersistent(v)).Reverse()) Object.DestroyImmediate(value);
                owned.Clear();
            }
        }
    }
}
