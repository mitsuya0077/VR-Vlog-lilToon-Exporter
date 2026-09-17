using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class PoseRegistrationTests
    {
        static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, field.FieldType.IsEnum ? Enum.Parse(field.FieldType, value.ToString()) : value);
        }
        static object Add(IList list)
        { var value = Activator.CreateInstance(list.GetType().GetGenericArguments()[0]); list.Add(value); return value; }
        static StateMachineBehaviour AnimateBody(AnimatorState state)
        {
            var tracking = state.AddStateMachineBehaviour(Find("VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl"));
            SetBodyAnimation(tracking); return tracking;
        }
        static void SetBodyAnimation(StateMachineBehaviour tracking)
        {
            foreach (var part in new[] { "trackingLeftHand", "trackingRightHand", "trackingHip", "trackingLeftFoot", "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers" }) Set(tracking, part, "Animation");
        }
        static Component Descriptor(GameObject root)
        {
            var type = Find("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (type == null) Assert.Ignore("Install VRChat SDK for registration tests.");
            var result = root.AddComponent(type); Set(result, "customExpressions", true); Set(result, "customizeAnimationLayers", true);
            Layers(result, "baseAnimationLayers", new[] { "Base", "Additive", "Gesture", "Action", "FX" });
            Layers(result, "specialAnimationLayers", new[] { "Sitting", "TPose", "IKPose" });
            return result;
        }
        static void Layers(Component descriptor, string fieldName, string[] types)
        {
            var field = descriptor.GetType().GetField(fieldName); var element = field.FieldType.GetElementType();
            var array = Array.CreateInstance(element, types.Length);
            for (var i = 0; i < types.Length; i++) { var layer = Activator.CreateInstance(element); Set(layer, "type", types[i]); Set(layer, "isDefault", true); array.SetValue(layer, i); }
            field.SetValue(descriptor, array);
        }
        [Test]
        public void AplSnapshotKeepsSourceNamesCategoriesExclusionsAndRejectsBeforeAnimation()
        {
            var type = Find(PoseExportSession.AplType);
            if (type == null) Assert.Ignore("Install APL runtime for the real serialized registration test.");
            using var f = new AttachmentConnectionTests.Fixture(); Descriptor(f.Source);
            var child = new GameObject("Library"); child.transform.SetParent(f.Source.transform, false);
            var component = child.AddComponent(type); var clip = HumanoidPoseTests.Clip(f.Source);
            try
            {
                var data = Activator.CreateInstance(type.GetField("data").FieldType); Set(component, "data", data);
                var category = Add((IList)PoseMenuResolver.Member(data, "categories")); Set(category, "name", "座り");
                var entry = Add((IList)PoseMenuResolver.Member(category, "poses")); Set(entry, "name", "登録名"); Set(entry, "animationClip", clip);
                for (var i = 0; i < 128; i++)
                {
                    var duplicate = Add((IList)PoseMenuResolver.Member(category, "poses")); Set(duplicate, "name", "重複" + i); Set(duplicate, "animationClip", clip);
                }
                var before = EditorJsonUtility.ToJson(component);
                var options = new PoseExportOptions(); options.Manual.Add(new ManualPose { Clip = clip });
                using (var snapshot = new PoseExportSession(f.Source, options))
                {
                    Assert.That(snapshot.Entries[0].Name, Is.EqualTo("登録名")); Assert.That(snapshot.Entries[0].Category, Is.EqualTo("座り"));
                    Assert.That(snapshot.Entries[0].Layers[0].Clip, Is.SameAs(clip));
                    var clone = Object.Instantiate(f.Source);
                    try
                    {
                        PoseExportSession.RemoveAplFromCopy(f.Source, clone);
                        Assert.That(clone.GetComponentInChildren(type), Is.Null); Assert.That(component, Is.Not.Null);
                        snapshot.CollectPrepared(clone);
                        Assert.That(snapshot.Entries.Count, Is.EqualTo(1), "Same clip/time/conditions has one pose with combined provenance.");
                        Assert.That(snapshot.Entries[0].Source, Does.Contain("APL").And.Contain("手動"));
                    }
                    finally { Object.DestroyImmediate(clone); }
                }
                Assert.That(EditorJsonUtility.ToJson(component), Is.EqualTo(before));
                using (var excluded = new PoseExportSession(f.Source, null, t => t == child.transform)) Assert.That(excluded.Entries.Count, Is.Zero);
                Set(entry, "beforeAnimationClip", clip);
                using (var unsupported = new PoseExportSession(f.Source, null)) Assert.That(unsupported.Entries[0].Error, Does.Contain("開始"));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, false, "Action")]
        [TestCase(false, false, false, "Action", true)]
        [TestCase(false, false, false, "Gesture", true, 0f)]
        [TestCase(false, false, false, "Gesture", true, 1f)]
        [TestCase(false, false, false, "Action", true, 0f)]
        [TestCase(false, false, false, "Gesture", false, 1f, true)]
        [TestCase(false, false, false, "Gesture", false, 1f, false, 1)]
        [TestCase(false, false, false, "Gesture", false, 1f, false, 2)]
        [TestCase(false, false, false, "Gesture", false, 1f, false, 0, true, TestName = "PropOnlyMenuDoesNotCaptureAnUnconditionalBodyLayer")]
        [TestCase(false, false, false, "Gesture", false, 1f, false, 0, false, true, TestName = "MaskedMenuDoesNotCaptureAnUnconditionalBodyLayer")]
        [TestCase(false, false, false, "Gesture", false, 1f, false, 0, false, false, 1, TestName = "EmptyWriteDefaultsStateDoesNotExposeLowerPose")]
        [TestCase(false, false, false, "Gesture", false, 1f, false, 0, false, false, 2, TestName = "NonBodyWriteDefaultsStateDoesNotExposeLowerPose")]
        [TestCase(false, false, false, "Gesture", false, 1f, false, 0, false, false, 3, TestName = "EmptyWriteDefaultsLayerRespectsControllerBodyBindings")]
        public void SubmenuAndMaGeneratedGestureMenuResolveOnlySelectedStaticPose(bool modularAvatar, bool defaultLocomotion, bool overrideClip, string layerType = "Gesture", bool crossLayerWeight = false, float controlWeight = 1f, bool unrelatedSolo = false, int unconditionalFallback = 0, bool propOnly = false, bool maskedSelection = false, int upperDefaults = 0, string trackingPart = null, string trackingLocation = "selected", string expressionCase = null, string historyCase = null, string weightBaseline = null)
        {
            using var f = new AttachmentConnectionTests.Fixture(); var descriptor = Descriptor(f.Source);
            var menuType = Find("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu");
            var menu = ScriptableObject.CreateInstance(menuType); var sub = ScriptableObject.CreateInstance(menuType);
            var controller = new AnimatorController(); var machine = new AnimatorStateMachine();
            var parameters = ScriptableObject.CreateInstance(Find("VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters"));
            var parameterField = parameters.GetType().GetField("parameters"); var parameterType = parameterField.FieldType.GetElementType();
            var parameterArray = Array.CreateInstance(parameterType, expressionCase == "Missing" ? 0 : expressionCase == "Duplicate" ? 2 : 1); var parameterRow = Activator.CreateInstance(parameterType);
            var expressionType = expressionCase == "Bool" || expressionCase == "BoolZero" || expressionCase == "TypeMismatch" ? "Bool" : expressionCase == "Float" || expressionCase == "FloatRange" ? "Float" : "Int";
            Set(parameterRow, "name", expressionCase == "Renamed" ? "OldPose" : "Pose"); Set(parameterRow, "valueType", expressionType);
            for (var i = 0; i < parameterArray.Length; i++) parameterArray.SetValue(parameterRow, i);
            parameterField.SetValue(parameters, parameterArray); Set(descriptor, "expressionParameters", parameters);
            var clip = propOnly ? new AnimationClip { name = "Prop" } : HumanoidPoseTests.Clip(f.Source); var unrelated = HumanoidPoseTests.Clip(f.Source, -80);
            if (historyCase != null && historyCase != "Mirror" && historyCase != "IK")
            {
                var arm = f.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.RightUpperArm);
                var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(arm, f.Source.transform), typeof(Transform), "localEulerAnglesRaw.z");
                AnimationUtility.SetEditorCurve(unrelated, binding, AnimationCurve.Constant(0, 1, 45));
                if (historyCase == "Covered") AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 0));
            }
            if (propOnly)
            {
                new GameObject("Prop").transform.SetParent(f.Source.transform, false);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Prop", typeof(Transform), "localEulerAnglesRaw.y"), AnimationCurve.Constant(0, 1, 30));
            }
            var owned = new List<Object>();
            var folderPath = "Assets/PoseRegistrationTest-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderPath.Substring("Assets/".Length));
            GameObject clone = null;
            try
            {
                controller.AddParameter("Pose", expressionCase == "TypeMismatch" ? AnimatorControllerParameterType.Int : (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), expressionType));
                var state = machine.AddState("Selected"); state.writeDefaultValues = historyCase == "Defaults"; state.motion = clip;
                if (trackingPart != "Absent" && !(trackingPart == "Animation" && trackingLocation != "selected")) AnimateBody(state);
                if (layerType == "Action" && !crossLayerWeight)
                {
                    var control = state.AddStateMachineBehaviour(Find("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl"));
                    Set(control, "layer", "Action"); Set(control, "goalWeight", 1f); Set(control, "blendDuration", 0f);
                }
                if (!crossLayerWeight)
                {
                    var idle = machine.AddState("Idle"); idle.writeDefaultValues = false;
                    var other = machine.AddState("Unselected"); other.writeDefaultValues = false; other.motion = unrelated;
                    other.mirror = historyCase == "Mirror"; other.iKOnFeet = historyCase == "IK";
                    var t = machine.AddAnyStateTransition(state); t.canTransitionToSelf = false; t.hasExitTime = false; t.duration = 0;
                    t.AddCondition(expressionType == "Bool" ? AnimatorConditionMode.If : expressionType == "Float" ? AnimatorConditionMode.Greater : AnimatorConditionMode.Equals, expressionType == "Int" ? unconditionalFallback == 1 ? 2 : 1 : 0, "Pose");
                    if (unconditionalFallback > 0)
                    {
                        var fallback = machine.AddAnyStateTransition(state); fallback.canTransitionToSelf = false; fallback.hasExitTime = false; fallback.duration = 0;
                    }
                    if (unrelatedSolo)
                    {
                        var solo = machine.AddAnyStateTransition(state); solo.canTransitionToSelf = false; solo.hasExitTime = false; solo.duration = 0; solo.solo = true;
                    }
                }
                AvatarMask sourceMask = null;
                if (maskedSelection || historyCase == "Masked")
                {
                    sourceMask = new AvatarMask(); owned.Add(sourceMask); sourceMask.AddTransformPath(f.Source.transform, true);
                    var rightArm = AnimationUtility.CalculateTransformPath(f.Source.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.RightUpperArm), f.Source.transform);
                    for (var i = 0; i < sourceMask.transformCount; i++) sourceMask.SetTransformActive(i, !maskedSelection && sourceMask.GetTransformPath(i) != rightArm);
                }
                controller.layers = new[] { new AnimatorControllerLayer { name = "Static pose", defaultWeight = 1, stateMachine = machine, avatarMask = sourceMask } };
                if (upperDefaults > 0)
                {
                    var upper = new AnimatorStateMachine(); var empty = upper.AddState("Default writes"); empty.writeDefaultValues = true;
                    var previous = upper.AddState("Previous"); previous.writeDefaultValues = false; previous.motion = upperDefaults == 3 ? null : clip;
                    if (upperDefaults == 2)
                    {
                        var propClip = new AnimationClip(); owned.Add(propClip); empty.motion = propClip;
                        AnimationUtility.SetEditorCurve(propClip, EditorCurveBinding.FloatCurve("Prop", typeof(Transform), "localEulerAnglesRaw.y"), AnimationCurve.Constant(0, 1, 30));
                    }
                    var t = upper.AddAnyStateTransition(empty); t.canTransitionToSelf = false; t.hasExitTime = false; t.duration = 0;
                    controller.layers = controller.layers.Concat(new[] { new AnimatorControllerLayer { name = "Upper default writes", defaultWeight = 1, stateMachine = upper } }).ToArray();
                }
                RuntimeAnimatorController effective = controller;
                if (overrideClip) { var replacement = new AnimatorOverrideController(controller); replacement[clip] = unrelated; owned.Add(replacement); effective = replacement; }
                var folder = Add((IList)PoseMenuResolver.Member(menu, "controls")); Set(folder, "name", "カテゴリー"); Set(folder, "type", "SubMenu"); Set(folder, "subMenu", sub);
                var toggle = Add((IList)PoseMenuResolver.Member(sub, "controls")); Set(toggle, "name", "メニュー名"); Set(toggle, "type", "Toggle");
                Set(toggle, "value", expressionCase == "BoolZero" ? 0f : expressionCase == "FloatRange" ? 2f : expressionCase == "FractionalInt" ? 1.5f : 1f);
                var parameter = Activator.CreateInstance(toggle.GetType().GetField("parameter").FieldType); Set(parameter, "name", "Pose"); Set(toggle, "parameter", parameter);
                // Supported fixtures use explicitly neutral other layers;
                // unresolved SDK defaults remain an external-input dependency.
                if (!defaultLocomotion)
                {
                    foreach (var key in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                    {
                        var field = descriptor.GetType().GetField(key); var array = (Array)field.GetValue(descriptor);
                        for (var i = 0; i < array.Length; i++)
                        {
                            var empty = new AnimatorController(); owned.Add(empty);
                            var layer = array.GetValue(i); Set(layer, "isDefault", false); Set(layer, "animatorController", empty); array.SetValue(layer, i);
                        }
                        field.SetValue(descriptor, array);
                    }
                }
                if (modularAvatar)
                {
                    var installType = Find("nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller");
                    var mergeType = Find("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
                    if (installType == null || mergeType == null) Assert.Ignore("Install MA/NDMF for generated menus.");
                    var go = new GameObject("Menu authoring"); go.transform.SetParent(f.Source.transform, false);
                    Set(go.AddComponent(installType), "menuToAppend", menu);
                    var merge = go.AddComponent(mergeType); Set(merge, "animator", effective); Set(merge, "layerType", "Gesture"); Set(merge, "pathMode", "Absolute");
                }
                else
                {
                    Set(descriptor, "expressionsMenu", menu);
                    var field = descriptor.GetType().GetField("baseAnimationLayers"); var array = (Array)field.GetValue(descriptor);
                    var index = layerType == "Action" ? 3 : 2;
                    var layer = array.GetValue(index); Set(layer, "isDefault", false); Set(layer, "animatorController", effective); array.SetValue(layer, index); field.SetValue(descriptor, array);
                    if (crossLayerWeight)
                    {
                        // FX has no body clips. Its menu-controlled behaviour
                        // enables an unconditional static pose in Action.
                        var fx = new AnimatorController(); var fxMachine = new AnimatorStateMachine(); owned.Add(fx);
                        fx.AddParameter("Pose", AnimatorControllerParameterType.Int);
                        var idle = fxMachine.AddState("Idle"); idle.writeDefaultValues = false;
                        var enable = fxMachine.AddState("Enable Action"); enable.writeDefaultValues = false;
                        if (trackingPart != "Absent") AnimateBody(enable);
                        var control = enable.AddStateMachineBehaviour(Find("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl"));
                        Set(control, "layer", layerType); Set(control, "goalWeight", controlWeight); Set(control, "blendDuration", 0f);
                        var t = fxMachine.AddAnyStateTransition(enable); t.canTransitionToSelf = false; t.hasExitTime = false; t.duration = 0; t.AddCondition(AnimatorConditionMode.Equals, 1, "Pose");
                        fx.layers = new[] { new AnimatorControllerLayer { name = "Enable body", defaultWeight = 1, stateMachine = fxMachine } };
                        if (weightBaseline != null)
                        {
                            var alwaysMachine = new AnimatorStateMachine(); var always = alwaysMachine.AddState("Already enabled"); always.writeDefaultValues = false;
                            var baseline = always.AddStateMachineBehaviour(Find("VRC.SDK3.Avatars.Components.VRCPlayableLayerControl"));
                            Set(baseline, "layer", layerType); Set(baseline, "goalWeight", controlWeight); Set(baseline, "blendDuration", 0f);
                            var alwaysLayer = new AnimatorControllerLayer { name = "Unconditional weight", defaultWeight = 1, stateMachine = alwaysMachine };
                            fx.layers = weightBaseline == "Before" ? new[] { alwaysLayer, fx.layers[0] } : new[] { fx.layers[0], alwaysLayer };
                        }
                        var fxLayer = array.GetValue(4); Set(fxLayer, "isDefault", false); Set(fxLayer, "animatorController", fx); array.SetValue(fxLayer, 4); field.SetValue(descriptor, array);
                    }
                    if (crossLayerWeight && controlWeight == 0 || propOnly || maskedSelection)
                    {
                        var lower = new AnimatorController(); owned.Add(lower); var lowerMachine = new AnimatorStateMachine();
                        var lowerState = lowerMachine.AddState("Lower pose"); lowerState.writeDefaultValues = false; lowerState.motion = unrelated;
                        lower.layers = new[] { new AnimatorControllerLayer { name = "Base pose", defaultWeight = 1, stateMachine = lowerMachine } };
                        var baseLayer = array.GetValue(0); Set(baseLayer, "isDefault", false); Set(baseLayer, "animatorController", lower); array.SetValue(baseLayer, 0); field.SetValue(descriptor, array);
                    }
                }
                if (trackingPart != null && trackingPart != "Absent")
                {
                    var trackingType = Find("VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl");
                    StateMachineBehaviour tracking;
                    if (trackingLocation == "machine") tracking = machine.AddStateMachineBehaviour(trackingType);
                    else if (trackingLocation == "nested")
                    {
                        var nested = machine.AddStateMachine("Container");
                        machine.states = machine.states.Where(s => s.state != state).ToArray();
                        nested.states = new[] { new ChildAnimatorState { state = state } }; nested.defaultState = state;
                        tracking = nested.AddStateMachineBehaviour(trackingType);
                    }
                    else if (trackingLocation == "sibling") tracking = machine.AddStateMachine("Unrelated").AddStateMachineBehaviour(trackingType);
                    else if (trackingLocation == "history") tracking = machine.states.Single(s => s.state.name == "Idle").state.AddStateMachineBehaviour(trackingType);
                    else if (trackingLocation == "emptyLayer")
                    {
                        var emptyMachine = new AnimatorStateMachine(); var empty = emptyMachine.AddState("Bodyless tracking"); empty.writeDefaultValues = false;
                        tracking = empty.AddStateMachineBehaviour(trackingType);
                        controller.layers = controller.layers.Concat(new[] { new AnimatorControllerLayer { name = "Tracking", stateMachine = emptyMachine, defaultWeight = 1 } }).ToArray();
                    }
                    else tracking = state.AddStateMachineBehaviour(trackingType);
                    SetBodyAnimation(tracking);
                    if (trackingPart == "NoChange") Set(tracking, "trackingLeftHand", "NoChange");
                    else if (trackingPart != "Animation") Set(tracking, trackingPart, "Tracking");
                }
                var before = EditorJsonUtility.ToJson(controller); var beforeMenu = EditorJsonUtility.ToJson(menu);
                var assets = new Object[] { controller, menu, sub, parameters, effective }.Concat(owned).Distinct().ToArray();
                var counter = 0;
                foreach (var asset in assets)
                    if (!EditorUtility.IsPersistent(asset)) AssetDatabase.CreateAsset(asset, folderPath + "/asset" + counter++ + ".asset");
                foreach (var dependency in EditorUtility.CollectDependencies(assets.OfType<RuntimeAnimatorController>().Cast<Object>().ToArray()))
                    if (dependency != null && !EditorUtility.IsPersistent(dependency)) AssetDatabase.AddObjectToAsset(dependency, controller);
                AssetDatabase.SaveAssets();
                foreach (var dependency in EditorUtility.CollectDependencies(new Object[] { f.Source }))
                    if (dependency != null && !(dependency is GameObject) && !(dependency is Component) && !EditorUtility.IsPersistent(dependency))
                        AssetDatabase.CreateAsset(dependency, folderPath + "/source" + counter++ + ".asset");
                AssetDatabase.SaveAssets();
                before = EditorJsonUtility.ToJson(controller); beforeMenu = EditorJsonUtility.ToJson(menu);
                if (overrideClip) Assert.That(ExpressionDependencies.Overrides(effective)[clip], Is.SameAs(unrelated));
                clone = Object.Instantiate(f.Source);
                using var preparation = NdmfExportPreparation.Prepare(f.Source, clone);
                var result = PoseMenuResolver.Read(clone);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(menu), Is.EqualTo(beforeMenu));
                if (historyCase == "Unbound" || historyCase == "Mirror" || historyCase == "IK") { Assert.That(result[0].Error, Does.Contain("Write Defaults")); return; }
                if (trackingLocation == "sibling") { Assert.That(result[0].Error, Does.Contain("Tracking Control")); return; }
                if (weightBaseline != null || crossLayerWeight && controlWeight == (layerType == "Action" ? 0 : 1))
                { Assert.That(result[0].Error, Does.Contain("確定")); return; }
                if (expressionCase != null && expressionCase != "Bool" && expressionCase != "Float")
                { Assert.That(result[0].Error, Does.Contain("Expression Parameters")); return; }
                if (trackingPart != null && trackingPart != "Animation" && trackingPart != "trackingHead" && trackingPart != "trackingEyes" && trackingPart != "trackingMouth")
                { Assert.That(result[0].Error, Does.Contain("Tracking Control")); return; }
                if (defaultLocomotion) { Assert.That(result[0].Error, Does.Contain("外部入力")); return; }
                if (upperDefaults > 0) { Assert.That(result[0].Error, Does.Contain("Write Defaults")); return; }
                if (unrelatedSolo || unconditionalFallback > 0 || propOnly || maskedSelection) { Assert.That(result[0].Error, Does.Contain("確定"), "A suppressed or irrelevant parameter edge cannot relate this menu to an unconditional pose."); return; }
                Assert.That(result[0].Error, Is.Null, result[0].Error);
                Assert.That(result[0].Name, Is.EqualTo("メニュー名")); Assert.That(result[0].Category, Is.EqualTo("カテゴリー"));
                Assert.That(result[0].Layers.Count, Is.EqualTo(1));
                if (overrideClip) Assert.That(result[0].Layers[0].Clip, Is.SameAs(unrelated));
                var sample = PoseSampling.Sample(clone, result[0]);
                Assert.That(sample.Bones.Single(b => b.Name == "leftUpperArm").Rotation[2] * (overrideClip || crossLayerWeight && controlWeight == 0 ? -1 : 1), Is.LessThan(0), "Use the effective selected clip, including overrides and a revealed lower layer.");
                Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(before)); Assert.That(EditorJsonUtility.ToJson(menu), Is.EqualTo(beforeMenu));
            }
            finally
            { if (clone != null) Object.DestroyImmediate(clone); AssetDatabase.DeleteAsset(folderPath); foreach (var item in owned.Concat(new Object[] { menu, sub, parameters, controller, machine, clip, unrelated })) if (item != null && !EditorUtility.IsPersistent(item)) Object.DestroyImmediate(item); }
        }

        [TestCase("trackingLeftHand")]
        [TestCase("trackingRightHand")]
        [TestCase("trackingHip")]
        [TestCase("trackingLeftFoot")]
        [TestCase("trackingRightFoot")]
        [TestCase("trackingLeftFingers")]
        [TestCase("trackingRightFingers")]
        [TestCase("trackingHead")]
        [TestCase("trackingEyes")]
        [TestCase("trackingMouth")]
        [TestCase("Animation")]
        [TestCase("Animation", "machine")]
        [TestCase("Animation", "nested")]
        [TestCase("Animation", "sibling")]
        [TestCase("NoChange")]
        [TestCase("Absent")]
        [TestCase("trackingLeftHand", "history")]
        [TestCase("trackingLeftHand", "machine")]
        [TestCase("trackingLeftHand", "emptyLayer")]
        public void MenuTrackingControlsRespectBodyAndHistory(string part, string location = "selected") =>
            SubmenuAndMaGeneratedGestureMenuResolveOnlySelectedStaticPose(false, false, false, trackingPart: part, trackingLocation: location);

        [TestCase("Missing")]
        [TestCase("Renamed")]
        [TestCase("Duplicate")]
        [TestCase("TypeMismatch")]
        [TestCase("FractionalInt")]
        [TestCase("BoolZero")]
        [TestCase("FloatRange")]
        [TestCase("Bool")]
        [TestCase("Float")]
        public void MenuInputsRequireMatchingExpressionDeclarations(string scenario) =>
            SubmenuAndMaGeneratedGestureMenuResolveOnlySelectedStaticPose(false, false, false, expressionCase: scenario);

        [TestCase("Unbound")]
        [TestCase("Defaults")]
        [TestCase("Covered")]
        [TestCase("Override")]
        [TestCase("Masked")]
        [TestCase("Mirror")]
        [TestCase("IK")]
        public void MenuPoseDoesNotRetainUnwrittenPredecessorBones(string scenario) =>
            SubmenuAndMaGeneratedGestureMenuResolveOnlySelectedStaticPose(false, false, scenario == "Override", historyCase: scenario);

        [TestCase("Before")]
        [TestCase("After")]
        public void MenuWeightControlDoesNotDuplicateAnUnconditionalControl(string order) =>
            SubmenuAndMaGeneratedGestureMenuResolveOnlySelectedStaticPose(false, false, false, "Action", true, weightBaseline: order);
    }
}
