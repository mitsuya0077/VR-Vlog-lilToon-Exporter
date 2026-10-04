using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class PreparedAnimationClipIdentityTests
    {
        string folder;
        GameObject avatar;
        AnimatorController controller;
        AnimationClip original, committed, unrelated;
        readonly List<Object> transient = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            var name = "__PreparedClipIdentity_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            controller.AddLayer("Registered player");
            original = Clip("Authored registered endpoint", 80);
            committed = Clip("Renamed committed endpoint", 80);
            unrelated = Clip(original.name, 30);
            avatar = new GameObject("Avatar");
            var lower = controller.layers[0].stateMachine.AddState("Lower"); lower.motion = unrelated; lower.writeDefaultValues = false;
            controller.layers[0].stateMachine.defaultState = lower;
            var player = controller.layers[1].stateMachine;
            var state = player.AddState("Registered endpoint"); state.motion = committed; state.writeDefaultValues = false;
            player.defaultState = state;
        }

        AnimationClip Clip(string name, float value)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Open"),
                AnimationCurve.Constant(0, 1, value));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar);
            foreach (var value in transient) if (value != null) Object.DestroyImmediate(value);
            transient.Clear();
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        // Use a fresh real NDMF registry instance, never its ambient global
        // registry. The public contract records exact replacement identities.
        internal static void RegisterReplacement(NdmfExportPreparation preparation, Transform avatarRoot,
            AnimationClip source, AnimationClip replacement, object registry = null)
        {
            if (registry == null)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("nadena.dev.ndmf.ObjectRegistry", false)).FirstOrDefault(t => t != null);
                if (type == null) Assert.Ignore("Install the real NDMF ObjectRegistry API.");
                registry = Activator.CreateInstance(type, new object[] { avatarRoot, null });
            }
            var contract = registry.GetType().GetInterfaces().Single(t => t.FullName == "nadena.dev.ndmf.IObjectRegistry");
            var getReference = contract.GetMethod("GetReference", new[] { typeof(Object), typeof(bool) });
            if (getReference == null) Assert.Ignore("The installed NDMF does not expose its replacement identity contract.");
            // The Object overload in some NDMF releases internally consults
            // the ambient registry. Build the reference on our own instance
            // and use its ObjectReference overload instead.
            var reference = getReference.Invoke(registry, new object[] { source, true });
            var register = contract.GetMethod("RegisterReplacedObject", new[] { reference.GetType(), typeof(Object) });
            if (register == null) Assert.Ignore("The installed NDMF does not expose its replacement identity contract.");
            register.Invoke(registry, new object[] { reference, replacement });
            typeof(NdmfExportPreparation).GetProperty("ObjectRegistry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(preparation, registry);
        }

        [Test]
        public void RegisteredReplacementSurvivesEffectiveOverrideAndNestedTreeWithoutMatchingNames()
        {
            controller.AddParameter("Weight", AnimatorControllerParameterType.Float);
            var tree = new BlendTree { name = "Native endpoint tree", blendType = BlendTreeType.Simple1D, blendParameter = "Weight" };
            tree.AddChild(original, 0); tree.AddChild(original, 1); AssetDatabase.AddObjectToAsset(tree, controller);
            controller.layers[1].stateMachine.defaultState.motion = tree;
            var runtime = new AnimatorOverrideController(controller); runtime[original] = committed; transient.Add(runtime);
            Assert.That(committed.name, Is.Not.EqualTo(original.name));
            using var preparation = new NdmfExportPreparation();
            RegisterReplacement(preparation, avatar.transform, original, committed);
            var source = EditorJsonUtility.ToJson(controller); var clip = EditorJsonUtility.ToJson(original);
            Assert.That(preparation.PreparedClipFor(original, runtime, 1), Is.SameAs(committed));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(source));
            Assert.That(EditorJsonUtility.ToJson(original), Is.EqualTo(clip));
        }

        [TestCase("missing")]
        [TestCase("wrong-origin")]
        [TestCase("same-name-decoy")]
        [TestCase("ambiguous")]
        [TestCase("outside-player")]
        public void RegisteredClipRequiresOneExactReplacementInsideItsPlayer(string kind)
        {
            using var preparation = new NdmfExportPreparation();
            if (kind == "same-name-decoy") committed.name = original.name;
            if (kind == "wrong-origin") RegisterReplacement(preparation, avatar.transform, unrelated, committed);
            if (kind == "ambiguous" || kind == "outside-player") RegisterReplacement(preparation, avatar.transform, original, committed);
            if (kind == "ambiguous")
            {
                var second = Clip("Another committed endpoint", 80);
                var state = controller.layers[1].stateMachine.AddState("Another registered state"); state.motion = second; state.writeDefaultValues = false;
                RegisterReplacement(preparation, avatar.transform, original, second, preparation.ObjectRegistry);
            }
            if (kind == "outside-player")
            {
                controller.layers[0].stateMachine.defaultState.motion = committed;
                controller.layers[1].stateMachine.defaultState.motion = unrelated;
            }
            var source = EditorJsonUtility.ToJson(controller); var clip = EditorJsonUtility.ToJson(original);
            Assert.That(preparation.PreparedClipFor(original, controller, 1), Is.Null,
                "Names, equivalent curves and replacements outside the selected slot do not establish its registered identity.");
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(source));
            Assert.That(EditorJsonUtility.ToJson(original), Is.EqualTo(clip));
        }

        [Test]
        public void ExactOriginalIdentityDoesNotRequireAnOptionalRegistry()
        {
            controller.layers[1].stateMachine.defaultState.motion = original;
            using var preparation = new NdmfExportPreparation();
            Assert.That(preparation.PreparedClipFor(original, controller, 1), Is.SameAs(original));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecordedChainCanReachAnOwnedIsolationCopyButCannotCycle(bool cycle)
        {
            var isolated = Clip("Owned isolation copy", 80);
            var bridge = Clip("Intermediate authoring copy", 80);
            var map = new Dictionary<AnimationClip, AnimationClip> { [committed] = bridge, [bridge] = isolated };
            if (cycle) map[isolated] = committed;
            AnimationClip Origin(AnimationClip current) => map.TryGetValue(current, out var value) ? value : null;
            var source = EditorJsonUtility.ToJson(controller);
            var resolved = PreparedAnimationClipIdentity.Resolve(original, isolated, controller, 1, Origin);
            if (cycle) Assert.That(resolved, Is.Null);
            else Assert.That(resolved, Is.SameAs(committed));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(source));
        }
    }
}
