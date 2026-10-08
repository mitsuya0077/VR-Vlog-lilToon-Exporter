using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class GestureBlendTreeTests
    {
        string folder;
        GameObject avatar;
        Mesh mesh;
        AnimatorController controller;
        AnimatorState selected;

        [SetUp]
        public void SetUp()
        {
            var name = "__GestureBlendTree_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            avatar = new GameObject("Avatar", typeof(Animator));
            var face = new GameObject("Face", typeof(SkinnedMeshRenderer)); face.transform.SetParent(avatar.transform, false);
            mesh = BaseShapeFixture.Create(); face.GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            face.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(0, 25);
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            controller.AddParameter("GestureLeftWeight", AnimatorControllerParameterType.Float);
            controller.AddParameter("GestureRightWeight", AnimatorControllerParameterType.Float);
            controller.AddParameter("EyeOption", AnimatorControllerParameterType.Bool);
            var root = controller.layers[0].stateMachine;
            root.defaultState = State(root, "Neutral", Clip("Neutral", 0));
            selected = State(root, "Hand Blend", null);
            var transition = root.defaultState.AddTransition(selected);
            transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureRight");
            transition.AddCondition(AnimatorConditionMode.If, 0, "EyeOption");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(folder);
        }

        AnimationClip Clip(string name, float weight)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 1, weight));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        BlendTree Tree(string name, BlendTreeType type, string x, string y = null)
        {
            var tree = new BlendTree { name = name, blendType = type, blendParameter = x, blendParameterY = y ?? "", useAutomaticThresholds = false };
            AssetDatabase.AddObjectToAsset(tree, controller); return tree;
        }

        static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
        {
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }

        VrChatExpressionMenu.Source Read(RuntimeAnimatorController runtime = null)
        {
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller")
                .ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var metadata = new VrChatExpressionMenu.Source { Controller = runtime ?? controller };
            VrChatGestureExpressions.Add(avatar, metadata);
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(avatar.GetComponent<Animator>().runtimeAnimatorController, Is.Null);
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(25));
            return metadata;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CustomSelectorRoutesStillDiscoverNativeHandWeightTreeCorners(bool nested)
        {
            controller.AddParameter("MenuFace", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            foreach (var transition in root.defaultState.transitions) root.defaultState.RemoveTransition(transition);
            var enter = root.defaultState.AddTransition(selected); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.Equals, 1, "MenuFace");
            var tree = Tree("Original hand corners behind a custom selector", BlendTreeType.FreeformCartesian2D,
                "GestureLeftWeight", "GestureRightWeight");
            tree.AddChild(Clip("Original resting corner", 20), Vector2.zero);
            tree.AddChild(Clip("Original left corner", 40), Vector2.right);
            tree.AddChild(Clip("Original right corner", 60), Vector2.up);
            tree.AddChild(Clip("Original combined corner", 80), Vector2.one);
            selected.motion = tree;
            if (nested)
            {
                controller.AddParameter("Configuration", AnimatorControllerParameterType.Float);
                var outer = Tree("Custom configured parent", BlendTreeType.Simple1D, "Configuration");
                outer.AddChild(tree, 0); selected.motion = outer;
            }
            var source = Read();
            Assert.That(source.Entries, Has.Count.EqualTo(4));
            Assert.That(source.Entries.All(entry => entry.Error == null && entry.Parameters["MenuFace"] == 1), Is.True);
            Assert.That(source.Entries.Select(entry => (entry.Parameters["GestureLeftWeight"], entry.Parameters["GestureRightWeight"])),
                Is.EquivalentTo(new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) }));
            foreach (var entry in source.Entries)
            {
                var selectors = entry.Parameters.Where(pair => !VrChatParameterDriver.BuiltIn.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                var native = Native(entry.Parameters, selectors: selectors);
                Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(root.name + "." + selected.name)));
                Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Weight).Within(.01));
                Assert.That(entry.Animation, Is.Empty);
            }
        }

        [TestCase("orphan")]
        [TestCase("unentered nested machine")]
        [TestCase("muted custom route")]
        [TestCase("shadowed Entry route")]
        public void UnreachableHandMotionsCannotManufactureGestureExpressions(string kind)
        {
            var root = controller.layers[0].stateMachine;
            var tree = Tree("Reachable original hand", BlendTreeType.Simple1D, "GestureRightWeight");
            tree.AddChild(Clip("Reachable first", 20), 0); tree.AddChild(Clip("Reachable second", 80), 1);
            selected.motion = tree;
            var owner = kind == "unentered nested machine" ? root.AddStateMachine("Unentered configuration") : root;
            var orphan = State(owner, "Unreachable hand motion", tree);
            if (owner != root) owner.defaultState = orphan;
            if (kind == "muted custom route")
            {
                controller.AddParameter("UnusedMenu", AnimatorControllerParameterType.Bool);
                var transition = root.defaultState.AddTransition(orphan); transition.mute = true;
                transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.If, 0, "UnusedMenu");
            }
            if (kind == "shadowed Entry route")
            {
                controller.AddParameter("UnusedMenu", AnimatorControllerParameterType.Bool);
                root.AddEntryTransition(root.defaultState);
                var later = root.AddEntryTransition(orphan);
                later.AddCondition(AnimatorConditionMode.If, 0, "UnusedMenu");
            }
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(2));
            Assert.That(entries.All(entry => entry.Name.Contains(selected.name)), Is.True);
            foreach (var entry in entries) AssertNative(entry);
            var dormantInputs = HandInputs(0, 1);
            if (kind == "muted custom route" || kind == "shadowed Entry route") dormantInputs["UnusedMenu"] = 1;
            Assert.That(Native(dormantInputs).Hash, Is.EqualTo(Animator.StringToHash(root.name + "." + selected.name)),
                "The original graph must retain its legal hand route even when the dormant custom input is selected.");
        }

        [Test]
        public void OrphanHandTreesCannotSpendTheExpressionRegistrationBudget()
        {
            var root = controller.layers[0].stateMachine;
            var tree = Tree("Original reachable hand budget", BlendTreeType.Simple1D, "GestureRightWeight");
            tree.AddChild(Clip("Original budget first", 20), 0); tree.AddChild(Clip("Original budget second", 80), 1);
            selected.motion = tree;
            for (var index = 0; index < 513; index++) State(root, "Orphan hand " + index, tree);
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(2), "Unreachable motions must not consume the 512-entry registration budget.");
            foreach (var entry in entries) AssertNative(entry);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeDefaultAndNestedEntryHandTreesRemainSelectable(bool nested)
        {
            var root = controller.layers[0].stateMachine;
            root.defaultState.transitions = Array.Empty<AnimatorStateTransition>();
            var path = root.name + "." + selected.name;
            if (nested)
            {
                controller.AddParameter("MenuFace", AnimatorControllerParameterType.Int);
                root.RemoveState(selected);
                var child = root.AddStateMachine("Authored configuration");
                var rest = State(child, "Nested rest", Clip("Nested rest", 0)); child.defaultState = rest;
                selected = State(child, "Native nested hand", null);
                var entry = child.AddEntryTransition(selected);
                entry.AddCondition(AnimatorConditionMode.Equals, 1, "MenuFace");
                var route = root.defaultState.AddTransition(child); route.hasExitTime = false; route.duration = 0;
                route.AddCondition(AnimatorConditionMode.Equals, 1, "MenuFace");
                path = root.name + "." + child.name + "." + selected.name;
            }
            else root.defaultState = selected;
            var tree = Tree("Native authored entry hand", BlendTreeType.Simple1D, "GestureRightWeight");
            tree.AddChild(Clip("Authored entry first", 20), 0); tree.AddChild(Clip("Authored entry second", 80), 1);
            selected.motion = tree;
            var entries = Read().Entries;
            Assert.That(entries, Has.Count.EqualTo(nested ? 2 : 1),
                "A native default needs only its nondefault hand point; a custom Entry route keeps both corners.");
            foreach (var entry in entries)
            {
                Assert.That(entry.Error, Is.Null, entry.Error);
                var selectors = entry.Parameters.Where(pair => !VrChatParameterDriver.BuiltIn.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                var native = Native(entry.Parameters, selectors: selectors);
                Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(path)));
                Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Weight).Within(.01));
                Assert.That(entry.Animation, Is.Empty);
            }
        }

        (float Weight, int Hash, float Blink, Dictionary<AnimationClip, float> Coefficients, float Phase) Native(IDictionary<string, float> inputs, RuntimeAnimatorController runtime = null,
            float? phase = null, IDictionary<string, float> selectors = null, float[] seekHistory = null,
            IDictionary<string, float> arrival = null)
        {
            var copy = Object.Instantiate(avatar); var graph = PlayableGraph.Create("Gesture tree independent native reference");
            try
            {
                var animator = copy.GetComponent<Animator>(); animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.fireEvents = false;
                var playable = AnimatorControllerPlayable.Create(graph, runtime ?? controller);
                AnimationPlayableOutput.Create(graph, "Native original graph", animator).SetSourcePlayable(playable);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                void Set(IDictionary<string, float> values)
                {
                    foreach (var pair in values)
                    {
                        var type = controller.parameters.Single(parameter => parameter.name == pair.Key).type;
                        if (type == AnimatorControllerParameterType.Bool) playable.SetBool(pair.Key, pair.Value != 0);
                        else if (type == AnimatorControllerParameterType.Int) playable.SetInteger(pair.Key, (int)pair.Value);
                        else playable.SetFloat(pair.Key, pair.Value);
                    }
                }
                Set(controller.parameters.Where(parameter => parameter.type != AnimatorControllerParameterType.Trigger)
                    .ToDictionary(parameter => parameter.name, parameter => parameter.type == AnimatorControllerParameterType.Bool ?
                        parameter.defaultBool ? 1f : 0f : parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat));
                graph.Play(); graph.Evaluate(0);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60);
                if (selectors != null) { Set(selectors); for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60); }
                if (arrival != null) { Set(arrival); for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60); }
                Set(inputs);
                for (var frame = 0; frame < 120; frame++) graph.Evaluate(1f / 60);
                foreach (var previous in seekHistory ?? Array.Empty<float>())
                {
                    playable.Play(playable.GetCurrentAnimatorStateInfo(0).fullPathHash, 0, previous);
                    graph.Evaluate(0); graph.Evaluate(0);
                    graph.Evaluate(1f / 60); graph.Evaluate(1f / 60);
                }
                if (phase.HasValue)
                {
                    var state = playable.GetCurrentAnimatorStateInfo(0);
                    playable.Play(state.fullPathHash, 0, phase.Value); graph.Evaluate(0); graph.Evaluate(0);
                }
                var renderer = copy.GetComponentInChildren<SkinnedMeshRenderer>();
                return (renderer.GetBlendShapeWeight(0), playable.GetCurrentAnimatorStateInfo(0).fullPathHash,
                    renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Blink")),
                    playable.GetCurrentAnimatorClipInfo(0).Where(item => item.clip != null && item.weight > 0)
                        .GroupBy(item => item.clip).ToDictionary(group => group.Key, group => group.Sum(item => item.weight)),
                    playable.GetCurrentAnimatorStateInfo(0).normalizedTime);
            }
            finally { graph.Destroy(); Object.DestroyImmediate(copy); }
        }

        void AssertNative(VrChatExpressionMenu.Entry entry)
        {
            Assert.That(entry.Error, Is.Null, entry.Name + ": " + entry.Error);
            Assert.That(entry.Parameters["GestureRight"], Is.EqualTo(1));
            Assert.That(entry.Parameters["EyeOption"], Is.EqualTo(1), "Custom gates must stay part of the native selection witness.");
            var native = Native(entry.Parameters);
            Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + selected.name)));
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Weight).Within(.01));
            Assert.That(entry.Animation, Is.Empty, "A fixed hand input exports the composed pose instead of the child clip's temporal program.");
        }

        void AssertNativeKnots(IList<VrChatExpressionMenu.Entry> entries, IEnumerable<Dictionary<string, float>> knots,
            RuntimeAnimatorController runtime = null)
        {
            var expected = knots.Select(inputs => Native(inputs, runtime).Weight).ToArray();
            var unique = expected.Aggregate(new List<float>(), (values, weight) =>
            { if (!values.Any(value => Mathf.Abs(value - weight) < .001f)) values.Add(weight); return values; });
            Assert.That(entries.Count, Is.EqualTo(unique.Count), "Native knot weights: " + string.Join(", ", expected) +
                "; exported weights: " + string.Join(", ", entries.Select(entry => entry.Values.FirstOrDefault()?.Weight.ToString() ?? entry.Error)));
            foreach (var weight in unique)
                Assert.That(entries.Any(entry => entry.Error == null && entry.Values.Any(value => value.Shape == "Face size" && Mathf.Abs(value.Weight - weight) < .01f)),
                    Is.True, "Every distinct original native knot pose must be present: " + weight);
        }

        Dictionary<string, float> HandInputs(float left, float right) => new Dictionary<string, float> {
            ["GestureRight"] = 1, ["EyeOption"] = 1, ["GestureLeftWeight"] = left, ["GestureRightWeight"] = right
        };

        [Test]
        public void TwoDimensionalHandKnotsKeepTheNativeGraphAndMatchingGestureGate()
        {
            var tree = Tree("Both hands", BlendTreeType.SimpleDirectional2D, "GestureLeftWeight", "GestureRightWeight");
            tree.AddChild(Clip("Leaf rest", 10), Vector2.zero);
            tree.AddChild(Clip("Leaf left", 40), Vector2.right);
            tree.AddChild(Clip("Leaf right", 85), Vector2.up);
            selected.motion = tree;
            controller.AddLayer("Composed upper offset");
            var upper = controller.layers[1].stateMachine;
            upper.defaultState = State(upper, "Configuration", Clip("Upper configuration", 5));
            var layers = controller.layers; layers[1].defaultWeight = 1; layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
            var entries = Read().Entries;
            AssertNativeKnots(entries, new[] { HandInputs(0, 0), HandInputs(1, 0), HandInputs(0, 1) });
            foreach (var entry in entries)
            {
                AssertNative(entry);
                Assert.That(entry.Name, Does.Contain("Hand Blend"));
                Assert.That(entry.Name, Does.Not.Contain("Leaf"), "Mixed child clips are never published as independent expressions.");
            }
            Assert.That(entries.All(entry => new[] { (0f, 0f), (1f, 0f), (0f, 1f) }
                .Contains((entry.Parameters["GestureLeftWeight"], entry.Parameters["GestureRightWeight"]))), Is.True);
        }

        [Test]
        public void NestedHandKnotsAreComposedAndEquivalentNativePosesAreDeduplicated()
        {
            var outer = Tree("Left hand", BlendTreeType.Simple1D, "GestureLeftWeight");
            var inner = Tree("Right hand", BlendTreeType.Simple1D, "GestureRightWeight");
            inner.AddChild(Clip("Inner rest", 40), 0); inner.AddChild(Clip("Inner selected", 85), 1);
            outer.AddChild(Clip("Outer rest", 10), 0); outer.AddChild(inner, 1); selected.motion = outer;
            var entries = Read().Entries;
            Assert.That(entries.Count, Is.EqualTo(3));
            foreach (var entry in entries) AssertNative(entry);
            var actual = entries.Select(entry => entry.Values.Single(value => value.Shape == "Face size").Weight).OrderBy(value => value).ToArray();
            var expected = new[] { 10f, 40f, 85f };
            for (var index = 0; index < expected.Length; index++)
                Assert.That(actual[index], Is.EqualTo(expected[index]).Within(.01));
        }

        [Test]
        public void SelectedTreeDriverRetainsItsNativeUpperLayerContribution()
        {
            var tree = Tree("Both hands", BlendTreeType.SimpleDirectional2D, "GestureLeftWeight", "GestureRightWeight");
            tree.AddChild(Clip("Leaf rest", 10), Vector2.zero);
            tree.AddChild(Clip("Leaf left", 40), Vector2.right);
            tree.AddChild(Clip("Leaf right", 85), Vector2.up); selected.motion = tree;
            controller.AddParameter("Relay", AnimatorControllerParameterType.Int);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Relay", 1));
            controller.AddLayer("Driven upper offset");
            var upper = controller.layers[1].stateMachine;
            var idleClip = new AnimationClip { name = "Upper idle" };
            var selectedClip = new AnimationClip { name = "Upper selected" };
            var blink = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink");
            AnimationUtility.SetEditorCurve(idleClip, blink, AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(selectedClip, blink, AnimationCurve.Constant(0, 1, 100));
            AssetDatabase.AddObjectToAsset(idleClip, controller); AssetDatabase.AddObjectToAsset(selectedClip, controller);
            upper.defaultState = State(upper, "Idle", idleClip);
            var active = State(upper, "Driven", selectedClip);
            var transition = upper.defaultState.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Relay");
            var layers = controller.layers; layers[1].defaultWeight = 1; layers[1].blendingMode = AnimatorLayerBlendingMode.Override; controller.layers = layers;
            var entries = Read().Entries;
            AssertNativeKnots(entries, new[] { HandInputs(0, 0), HandInputs(1, 0), HandInputs(0, 1) }
                .Select(inputs => new Dictionary<string, float>(inputs) { ["Relay"] = 1 }));
            foreach (var entry in entries)
            {
                Assert.That(entry.Error, Is.Null, entry.Error);
                Assert.That(entry.Parameters.ContainsKey("Relay"), Is.False, "A driver output must not become an independently selected user input.");
                // SDK parameter-driver delegates do not run outside VRChat.
                // Apply its single documented Set operation to the unmodified
                // original native graph as an independent comparison.
                var nativeInputs = new Dictionary<string, float>(entry.Parameters) { ["Relay"] = 1 };
                var expected = Native(nativeInputs);
                Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                    Is.EqualTo(expected.Weight).Within(.01));
                Assert.That(entry.Values.Single(value => value.Shape == "Blink").Weight, Is.EqualTo(expected.Blink).Within(.01));
                var withoutCallback = Native(entry.Parameters);
                Assert.That(Mathf.Abs(expected.Blink - withoutCallback.Blink), Is.GreaterThan(1),
                    "Dropping the selected state callback must produce a different native pose.");
            }
        }

        [Test]
        public void ATreeWithAnUndeclaredInputKeepsAnActionableErrorWithoutPublishingLeaves()
        {
            var tree = Tree("Malformed input", BlendTreeType.Simple1D, "Undeclared hand signal");
            tree.AddChild(Clip("Leaf rest", 10), 0); tree.AddChild(Clip("Leaf selected", 85), 1); selected.motion = tree;
            var entries = Read().Entries;
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries.Single().Error, Does.Contain("Undeclared hand signal"));
            Assert.That(entries.Single().Values, Is.Empty);
        }

        [Test]
        public void TreeDependenciesIgnoreUnusedAxisAndChildDefaultsButRejectAnActualUndeclaredDirectInput()
        {
            var tree = Tree("Actual one dimensional input", BlendTreeType.Simple1D, "GestureRightWeight", "Unused axis");
            tree.AddChild(Clip("First", 10), 0); tree.AddChild(Clip("Second", 85), 1); selected.motion = tree;
            var unknown = new List<string>();
            var dependencies = ExpressionDependencies.Inspect(controller, null,
                new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true);
            Assert.That(dependencies[0].Reads, Does.Contain("GestureRightWeight"));
            Assert.That(dependencies[0].Reads, Does.Not.Contain("Unused axis"));
            Assert.That(dependencies[0].Reads, Does.Not.Contain("Blend"), "A non-Direct child's serialized default is never a native input.");
            tree.blendType = BlendTreeType.Direct;
            var children = tree.children;
            for (var index = 0; index < children.Length; index++) children[index].directBlendParameter = "Actual undeclared direct";
            tree.children = children;
            var entries = Read().Entries;
            Assert.That(entries.Count, Is.EqualTo(1)); Assert.That(entries.Single().Error, Does.Contain("Actual undeclared direct"));
            Assert.That(entries.Single().Values, Is.Empty);
        }

        [Test]
        public void TargetedHandDiscoveryCannotSpendItsBudgetOnUnrelatedCompoundPriorityBranches()
        {
            var machine = controller.layers[0].stateMachine;
            machine.defaultState.transitions = Array.Empty<AnimatorStateTransition>();
            for (var index = 0; index < 64; index++)
            {
                var first = "Other selector A " + index; var second = "Other selector B " + index;
                controller.AddParameter(first, AnimatorControllerParameterType.Bool); controller.AddParameter(second, AnimatorControllerParameterType.Bool);
                var unrelated = State(machine, "Other face " + index, Clip("Other face " + index, 10 + index));
                var edge = machine.AddAnyStateTransition(unrelated); edge.hasExitTime = false; edge.duration = 0; edge.canTransitionToSelf = true;
                edge.AddCondition(AnimatorConditionMode.Equals, 0, "GestureRight");
                edge.AddCondition(AnimatorConditionMode.If, 0, first); edge.AddCondition(AnimatorConditionMode.If, 0, second);
            }
            var wanted = machine.AddAnyStateTransition(selected); wanted.hasExitTime = false; wanted.duration = 0; wanted.canTransitionToSelf = false;
            wanted.AddCondition(AnimatorConditionMode.Equals, 1, "GestureRight"); wanted.AddCondition(AnimatorConditionMode.If, 0, "EyeOption");
            var tree = Tree("Targeted native tree", BlendTreeType.Simple1D, "GestureRightWeight");
            tree.AddChild(Clip("Wanted first", 10), 0); tree.AddChild(Clip("Wanted second", 85), 1); selected.motion = tree;
            var entries = VrChatFxExpressions.ReadGestureState(avatar, new VrChatExpressionMenu.Source { Controller = controller }, 0, selected, "Targeted native tree");
            Assert.That(entries.Count, Is.EqualTo(2));
            foreach (var entry in entries) AssertNative(entry);
            Assert.That(entries.All(entry => entry.Parameters.Keys.All(name => !name.StartsWith("Other selector", StringComparison.Ordinal))), Is.True,
                "Conditions disproved at the same native decision must not become unrelated user assignments.");
        }

        [Test]
        public void AuthoredSelectorBeforeTheHandReachesItsNativeNestedTreeWithoutForcingTheTargetState()
        {
            controller.AddParameter("FaceMode", AnimatorControllerParameterType.Int);
            var parameters = controller.parameters; parameters.Single(parameter => parameter.name == "FaceMode").defaultInt = 1;
            controller.parameters = parameters;
            var root = controller.layers[0].stateMachine; root.defaultState.transitions = Array.Empty<AnimatorStateTransition>();
            root.RemoveState(selected);
            var first = root.AddStateMachine("First configuration"); var second = root.AddStateMachine("Second configuration");
            first.defaultState = State(first, "Rest", Clip("First rest", 10));
            second.defaultState = State(second, "Rest", Clip("Second rest", 20));
            var trapped = State(first, "Held original hand", Clip("First held hand", 35));
            selected = State(second, "Selected native hand", null);
            var tree = Tree("Second native clock", BlendTreeType.Simple1D, "GestureRightWeight");
            var low = MotionTimeClip("Second low", 2, 1, 10, 60, 10); var high = MotionTimeClip("Second high", 2, 1, 20, 90, 20);
            foreach (var clip in new[] { low, high }) { var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings); }
            tree.AddChild(low, 0); tree.AddChild(high, 1); selected.motion = tree;
            // A real authored startup state chooses the nested configuration.
            // Each nested Exit returns to it; no direct sibling-machine jump
            // can bypass the state's earlier hand-transition priority.
            var firstEntry = root.defaultState.AddTransition(first); firstEntry.hasExitTime = false; firstEntry.duration = 0;
            firstEntry.AddCondition(AnimatorConditionMode.Equals, 1, "FaceMode");
            var secondEntry = root.defaultState.AddTransition(second); secondEntry.hasExitTime = false; secondEntry.duration = 0;
            secondEntry.AddCondition(AnimatorConditionMode.Equals, 2, "FaceMode");
            root.AddStateMachineTransition(first, root.defaultState);
            root.AddStateMachineTransition(second, root.defaultState);
            foreach (var pair in new[] { (Machine: first, Hand: trapped, Mode: 1), (Machine: second, Hand: selected, Mode: 2) })
            {
                var hand = pair.Machine.defaultState.AddTransition(pair.Hand); hand.hasExitTime = false; hand.duration = 0;
                hand.AddCondition(AnimatorConditionMode.Equals, 1, "GestureRight"); hand.AddCondition(AnimatorConditionMode.If, 0, "EyeOption");
                hand.AddCondition(AnimatorConditionMode.Greater, .2f, "GestureRightWeight");
                var exit = pair.Machine.defaultState.AddExitTransition(); exit.hasExitTime = false; exit.duration = 0;
                exit.AddCondition(AnimatorConditionMode.NotEqual, pair.Mode, "FaceMode");
            }
            var inputs = HandInputs(0, 1); inputs.Add("FaceMode", 2);
            var selectors = new Dictionary<string, float> { ["FaceMode"] = 2, ["EyeOption"] = 1 };
            Assert.That(Native(new Dictionary<string, float>()).Hash,
                Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + first.name + ".Rest")),
                "The original native startup must enter the first configuration before either selection history.");
            var simultaneous = Native(inputs); var staged = Native(inputs, selectors: selectors);
            Assert.That(simultaneous.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + first.name + "." + trapped.name)),
                "Applying both inputs simultaneously must reproduce the original first-state priority trap.");
            Assert.That(staged.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + second.name + "." + selected.name)),
                "The original graph must reach the second tree through its authored selector and exit route.");
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller").ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var source = new VrChatExpressionMenu.Source { Controller = controller }; source.Defaults.Add("FaceMode", 1);
            source.ExpressionParameters.Add("FaceMode"); source.MenuInputs.Add("EyeOption");
            var entries = VrChatFxExpressions.ReadGestureState(avatar, source, 0, selected, "Staged native hand");
            Assert.That(entries, Is.Not.Empty); Assert.That(entries.All(entry => entry.Error == null), Is.True, string.Join("; ", entries.Select(entry => entry.Error)));
            foreach (var entry in entries)
                foreach (var phase in new[] { 0f, .17f, .5f, .83f, .999f })
                {
                    var native = Native(entry.Parameters, phase: phase, selectors: selectors);
                    Assert.That(entry.Animation.Single(channel => channel.Shape == "Face size").Curve.Evaluate(phase * entry.Duration),
                        Is.EqualTo(native.Weight).Within(.02));
                }
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
        }

        AnimationClip MotionTimeClip(string name, float length, float middleTime, float first, float middle, float last)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                new AnimationCurve(new Keyframe(0, first), new Keyframe(middleTime, middle), new Keyframe(length, last)));
            AssetDatabase.AddObjectToAsset(clip, controller); return clip;
        }

        [Test]
        public void AHandTreeHeldCornerUsesItsAuthoredArrivalBeforeTheFinalBothHandInputs()
        {
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var root = controller.layers[0].stateMachine;
            root.defaultState.transitions = Array.Empty<AnimatorStateTransition>();
            foreach (var left in new[] { true, false })
            {
                var transition = root.defaultState.AddTransition(selected); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
                transition.AddCondition(AnimatorConditionMode.Equals, 1, "GestureRight");
                transition.AddCondition(AnimatorConditionMode.If, 0, "EyeOption");
                transition.AddCondition(AnimatorConditionMode.Greater, .2f, left ? "GestureLeftWeight" : "GestureRightWeight");
                transition.AddCondition(AnimatorConditionMode.Less, .01f, left ? "GestureRightWeight" : "GestureLeftWeight");
            }
            var exit = selected.AddTransition(root.defaultState); exit.hasExitTime = false; exit.duration = 0;
            exit.AddCondition(AnimatorConditionMode.Less, .01f, "GestureLeftWeight");
            exit.AddCondition(AnimatorConditionMode.Less, .01f, "GestureRightWeight");
            var tree = Tree("Authored held corners", BlendTreeType.FreeformCartesian2D, "GestureLeftWeight", "GestureRightWeight");
            tree.AddChild(Clip("Zero corner", 10), Vector2.zero);
            tree.AddChild(Clip("Left corner", 40), Vector2.right);
            tree.AddChild(Clip("Right corner", 60), Vector2.up);
            tree.AddChild(Clip("Both corner", 90), Vector2.one); selected.motion = tree;
            var final = HandInputs(1, 1); final.Add("GestureLeft", 1);
            var arrival = new Dictionary<string, float>(final) { ["GestureRightWeight"] = 0 };
            var selectors = new Dictionary<string, float> { ["EyeOption"] = 1 };
            var direct = Native(final, selectors: selectors); var held = Native(final, selectors: selectors, arrival: arrival);
            var target = Animator.StringToHash(controller.layers[0].name + "." + selected.name);
            Assert.That(direct.Hash, Is.Not.EqualTo(target), "Both inputs cannot bypass the authored one-hand entry gate.");
            Assert.That(held.Hash, Is.EqualTo(target)); Assert.That(held.Weight, Is.EqualTo(90).Within(.01));
            var entries = VrChatFxExpressions.ReadGestureState(avatar, new VrChatExpressionMenu.Source { Controller = controller }, 0, selected, "Native held hand");
            Assert.That(entries.All(entry => entry.Error == null), Is.True, string.Join("; ", entries.Select(entry => entry.Error)));
            var corner = entries.Single(entry => entry.Parameters["GestureLeftWeight"] == 1 && entry.Parameters["GestureRightWeight"] == 1);
            Assert.That(corner.Parameters["GestureLeft"], Is.EqualTo(1)); Assert.That(corner.Parameters["GestureRight"], Is.EqualTo(1));
            Assert.That(corner.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(held.Weight).Within(.02));
            Assert.That(corner.Animation, Is.Empty); Assert.That(corner.Duration, Is.EqualTo(0)); Assert.That(corner.Loop, Is.False);
            Assert.That(entries.Any(entry => entry.Parameters["GestureLeftWeight"] == 0 && entry.Parameters["GestureRightWeight"] == 0), Is.False,
                "A zero-hand knot that immediately exits the original target state is not a held facial selection.");
        }

        [Test]
        public void AnimatedHandTreePreservesNativeMixturesSparseReferenceAndSelectedUpperSupport()
        {
            var left = MotionTimeClip("Moving left", 2, 1, 10, 60, 10);
            var right = MotionTimeClip("Moving right", 2, 1, 90, 20, 90);
            var blink = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink");
            AnimationUtility.SetEditorCurve(left, blink, new AnimationCurve(new Keyframe(0, 20), new Keyframe(1, 80), new Keyframe(2, 20)));
            foreach (var clip in new[] { left, right })
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            var tree = Tree("Native animated mixture", BlendTreeType.Simple1D, "GestureRightWeight");
            tree.AddChild(left, -.05f); tree.AddChild(right, 1.05f); selected.motion = tree;
            avatar.GetComponentInChildren<SkinnedMeshRenderer>().SetBlendShapeWeight(mesh.GetBlendShapeIndex("Blink"), 20);
            controller.AddParameter("Relay", AnimatorControllerParameterType.Int);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Relay", 1));
            controller.AddLayer("Input and callback configuration");
            var upper = controller.layers[1].stateMachine;
            var idle = Clip("Inactive upper", 5); var activeClip = Clip("Selected upper", 65);
            AnimationUtility.SetEditorCurve(idle, blink, AnimationCurve.Constant(0, 1, 0));
            AnimationUtility.SetEditorCurve(activeClip, blink, AnimationCurve.Constant(0, 1, 100));
            upper.defaultState = State(upper, "Idle", idle);
            var active = State(upper, "Input selected", activeClip);
            var transition = upper.defaultState.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Relay");
            transition.AddCondition(AnimatorConditionMode.If, 0, "EyeOption");
            var layers = controller.layers; layers[1].defaultWeight = .25f; controller.layers = layers;
            var mixtureInputs = HandInputs(0, 0); mixtureInputs["Relay"] = 1;
            var sparseSelectors = new Dictionary<string, float> { ["EyeOption"] = 1 };
            var fresh = Native(mixtureInputs, phase: .5f, selectors: sparseSelectors);
            var ordered = Native(mixtureInputs, phase: .5f, selectors: sparseSelectors, seekHistory: new[] { 0f, .125f, .25f, .375f });
            Assert.That(fresh.Phase, Is.EqualTo(ordered.Phase).Within(.000001f));
            Assert.That(fresh.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + selected.name)));
            Assert.That(ordered.Hash, Is.EqualTo(fresh.Hash));
            Assert.That(Mathf.Abs(fresh.Blink - ordered.Blink), Is.GreaterThan(.02f),
                "Identical selected inputs and actual phase must expose the original sparse WD-off history dependence.");
            TestContext.WriteLine("Original sparse WD-off same-phase histories: fresh Blink=" + fresh.Blink +
                ", ordered Blink=" + ordered.Blink + ", delta=" + Mathf.Abs(fresh.Blink - ordered.Blink) + ", phase=" + fresh.Phase);
            var unsupported = Read().Entries;
            Assert.That(unsupported, Is.Not.Empty);
            Assert.That(unsupported.All(entry => entry.Error != null && entry.Error.Contains("履歴") &&
                entry.Values.Count == 0 && entry.Animation.Count == 0), Is.True,
                "The native history-dependent program must remain an actionable diagnostic, never an invented single timeline: " +
                string.Join("; ", unsupported.Select(entry => entry.Error)));
            // The positive calibration fixture has a fixed native reference.
            // The original WD-off history measurement above is kept separate
            // from the WD-on program whose analytic trajectory is validated.
            selected.writeDefaultValues = true;
            var mixture = Native(mixtureInputs, phase: .17f, selectors: new Dictionary<string, float> { ["EyeOption"] = 1 });
            Assert.That(mixture.Coefficients[left], Is.GreaterThan(0)); Assert.That(mixture.Coefficients[right], Is.GreaterThan(0),
                "The original native graph must actually blend both authored timelines at the tested hand boundary.");
            var entries = Read().Entries;
            Assert.That(entries.Count, Is.EqualTo(2));
            foreach (var entry in entries)
            {
                Assert.That(entry.Error, Is.Null, entry.Error); Assert.That(entry.Loop, Is.True);
                Assert.That(entry.Duration, Is.EqualTo(2).Within(.0001f)); Assert.That(entry.Animation, Is.Not.Empty);
                Assert.That(entry.Name, Does.Not.Contain("Moving left")); Assert.That(entry.Name, Does.Not.Contain("Moving right"));
                Assert.That(entry.Parameters.ContainsKey("Relay"), Is.False);
                var inputs = new Dictionary<string, float>(entry.Parameters) { ["Relay"] = 1 };
                var selectors = entry.Parameters.Where(pair => !VrChatParameterDriver.BuiltIn.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var phase in new[] { 0f, .17f, .5f, .83f, .999f })
                {
                    var native = Native(inputs, phase: phase, selectors: selectors);
                    Assert.That(native.Phase, Is.EqualTo(phase).Within(.0001f));
                    Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + selected.name)));
                    double Exported(string shape) => entry.Animation.SingleOrDefault(channel => channel.Shape == shape)?.Curve.Evaluate(native.Phase * entry.Duration) ??
                        entry.Values.Single(value => value.Shape == shape).Weight;
                    Assert.That(Exported("Face size"), Is.EqualTo(native.Weight).Within(.02), entry.Name + " / phase " + phase);
                    Assert.That(Exported("Blink"), Is.EqualTo(native.Blink).Within(.02), entry.Name + " / sparse phase " + phase);
                }
                Assert.That(Mathf.Abs(Native(inputs, phase: .5f, selectors: selectors).Blink - Native(entry.Parameters, phase: .5f, selectors: selectors).Blink), Is.GreaterThan(1),
                    "Removing the selected callback must change the original native contribution.");
            }
        }

        [TestCase("duration")]
        [TestCase("loop")]
        [TestCase("child speed")]
        public void AnimatedHandTreesRejectIncoherentChildTimelines(string damage)
        {
            var left = MotionTimeClip("Left clock", 2, 1, 10, 60, 10);
            var right = MotionTimeClip("Right clock", damage == "duration" ? 4 : 2, 1, 90, 20, 90);
            foreach (var clip in new[] { left, right })
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = damage != "loop" || clip == left;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            var tree = Tree("Unsafe animated mixture", BlendTreeType.Simple1D, "GestureRightWeight");
            tree.AddChild(left, -.05f); tree.AddChild(right, 1.05f); selected.motion = tree;
            if (damage == "child speed") { var children = tree.children; children[0].timeScale = .5f; tree.children = children; }
            var entries = Read().Entries;
            Assert.That(entries, Is.Not.Empty);
            Assert.That(entries.All(entry => entry.Error != null && entry.Values.Count == 0 && entry.Animation.Count == 0), Is.True,
                "Mixed clocks must not be published as independent leaf expressions.");
        }

        [Test]
        public void SparseBindingCalibrationKeepsAReusedLeafInTheNativeUpperSupportGraph()
        {
            var moving = MotionTimeClip("Moving sparse binding", 2, 1, 10, 60, 10);
            var blink = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink");
            AnimationUtility.SetEditorCurve(moving, blink, new AnimationCurve(new Keyframe(0, 20), new Keyframe(1, 80), new Keyframe(2, 20)));
            var settings = AnimationUtility.GetAnimationClipSettings(moving); settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(moving, settings);
            var shared = Clip("Shared static tree and upper leaf", 65);
            AnimationUtility.SetEditorCurve(shared, blink, AnimationCurve.Constant(0, 2, 20));
            AnimationUtility.SetEditorCurve(shared, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 2, 65));
            var missing = Clip("No blink binding", 5);
            AnimationUtility.SetEditorCurve(missing, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                AnimationCurve.Constant(0, 2, 5));
            foreach (var name in new[] { "MovingMix", "SharedMix", "SparseMix" }) controller.AddParameter(name, AnimatorControllerParameterType.Float);
            var tree = Tree("Three native leaves", BlendTreeType.Direct, "Unused axis");
            tree.children = new[] {
                new ChildMotion { motion = moving, directBlendParameter = "MovingMix", timeScale = 1 },
                new ChildMotion { motion = shared, directBlendParameter = "SharedMix", timeScale = 1 },
                new ChildMotion { motion = missing, directBlendParameter = "SparseMix", timeScale = 1 }
            }; selected.motion = tree; selected.writeDefaultValues = true;
            controller.AddParameter("Relay", AnimatorControllerParameterType.Int);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Relay", 1));
            controller.AddLayer("Reused original native support"); var upper = controller.layers[1].stateMachine;
            var inactive = Clip("Inactive upper", 5); AnimationUtility.SetEditorCurve(inactive, blink, AnimationCurve.Constant(0, 1, 0));
            upper.defaultState = State(upper, "Idle", inactive); var active = State(upper, "Selected original shared clip", shared);
            var transition = upper.defaultState.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Relay"); transition.AddCondition(AnimatorConditionMode.If, 0, "EyeOption");
            var layers = controller.layers; layers[1].defaultWeight = .25f; controller.layers = layers;
            var before = AssetDatabase.LoadAllAssetsAtPath(folder + "/FX.controller").ToDictionary(asset => asset, asset => EditorJsonUtility.ToJson(asset));
            var entry = new VrChatExpressionMenu.Entry { Name = "Native sparse mixture with shared support" };
            foreach (var pair in HandInputs(0, 0)) entry.Parameters.Add(pair.Key, pair.Value);
            entry.Parameters.Add("MovingMix", .3f); entry.Parameters.Add("SharedMix", .3f); entry.Parameters.Add("SparseMix", .4f);
            var inputs = new Dictionary<string, float>(entry.Parameters) { ["Relay"] = 1 };
            var selectors = entry.Parameters.Where(pair => !VrChatParameterDriver.BuiltIn.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            var original = Native(inputs, phase: .17f, selectors: selectors);
            Assert.That(original.Coefficients.Keys, Is.EquivalentTo(new[] { moving, shared, missing }),
                "The original graph must actually retain the sparse moving mix and its shared leaf.");
            AnimatedGestureTree.Read(avatar, new VrChatExpressionMenu.Source { Controller = controller }, 0, selected, entry, null);
            Assert.That(entry.Animation, Is.Not.Empty);
            foreach (var phase in new[] { 0f, .17f, .5f, .83f, .999f })
            {
                var native = Native(inputs, phase: phase, selectors: selectors);
                Assert.That(native.Phase, Is.EqualTo(phase).Within(.0001f));
                double Exported(string shape) => entry.Animation.SingleOrDefault(channel => channel.Shape == shape)?.Curve.Evaluate(native.Phase * entry.Duration) ??
                    entry.Values.Single(value => value.Shape == shape).Weight;
                Assert.That(Exported("Face size"), Is.EqualTo(native.Weight).Within(.02));
                Assert.That(Exported("Blink"), Is.EqualTo(native.Blink).Within(.02), "Calibration must leave the reused upper clip's native offset unchanged.");
            }
            Assert.That(Mathf.Abs(original.Blink - Native(entry.Parameters, phase: .17f, selectors: selectors).Blink), Is.GreaterThan(1));
            foreach (var pair in before) Assert.That(EditorJsonUtility.ToJson(pair.Key), Is.EqualTo(pair.Value), pair.Key.name);
            Assert.That(avatar.GetComponent<Animator>().runtimeAnimatorController, Is.Null);
        }

        [Test]
        public void AStaticFaceAtTheSameInitialPoseCannotSuppressAnAnimatedTreeSelection()
        {
            var still = Clip("Still initial pose", 10);
            var moving = MotionTimeClip("Moving same initial pose", 2, 1, 10, 80, 10);
            foreach (var clip in new[] { still, moving })
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            var tree = Tree("Static and moving", BlendTreeType.Simple1D, "GestureRightWeight");
            tree.AddChild(still, 0); tree.AddChild(moving, 1); selected.motion = tree;
            var entries = Read().Entries;
            Assert.That(entries.Count, Is.EqualTo(2)); Assert.That(entries.All(entry => entry.Error == null), Is.True,
                string.Join("; ", entries.Select(entry => entry.Error)));
            Assert.That(entries.Count(entry => entry.Animation.Count > 0), Is.EqualTo(1));
            var animated = entries.Single(entry => entry.Animation.Count > 0);
            Assert.That(animated.Animation.Single(channel => channel.Shape == "Face size").Curve.Evaluate(animated.Duration * .5f),
                Is.EqualTo(Native(animated.Parameters, phase: .5f).Weight).Within(.02));
        }

        [TestCase("GestureRightWeight")]
        [TestCase("FaceIntensity")]
        public void MotionTimeGesturePublishesNativeKeySnapshotsInsteadOfAutoplayingTheClip(string input)
        {
            if (input == "FaceIntensity") controller.AddParameter(input, AnimatorControllerParameterType.Float);
            selected.motion = MotionTimeClip("Authored motion time", 2, .8f, 10, 45, 85);
            selected.timeParameterActive = true; selected.timeParameter = input;
            controller.AddParameter("Relay", AnimatorControllerParameterType.Int);
            ParameterDriverExpressionTests.Driver(selected, ParameterDriverExpressionTests.Op("Set", "Relay", 1));
            controller.AddLayer("Driven upper offset");
            var upper = controller.layers[1].stateMachine;
            upper.defaultState = State(upper, "Idle", Clip("Upper idle", 0));
            var active = State(upper, "Driven", Clip("Upper selected", 5));
            var transition = upper.defaultState.AddTransition(active); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Equals, 1, "Relay");
            var layers = controller.layers; layers[1].defaultWeight = 1; layers[1].blendingMode = AnimatorLayerBlendingMode.Additive; controller.layers = layers;
            var entries = Read().Entries;
            var knots = new[] { 0f, .4f, 1f }.Select(value => {
                var inputs = HandInputs(0, input == "GestureRightWeight" ? value : 1);
                inputs[input] = value; inputs["Relay"] = 1; return inputs;
            }).ToArray();
            AssertNativeKnots(entries, knots);
            Assert.That(entries.Select(entry => entry.Parameters[input]), Is.EquivalentTo(new[] { 0f, .4f, 1f }));
            foreach (var entry in entries)
            {
                Assert.That(entry.Error, Is.Null, entry.Error); Assert.That(entry.Animation, Is.Empty);
                Assert.That(entry.Duration, Is.EqualTo(0)); Assert.That(entry.Loop, Is.False);
                var inputs = new Dictionary<string, float>(entry.Parameters) { ["Relay"] = 1 };
                var native = Native(inputs);
                Assert.That(native.Hash, Is.EqualTo(Animator.StringToHash(controller.layers[0].name + "." + selected.name)));
                Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(native.Weight).Within(.01));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AConstantGestureAndDefaultFxClockDoNotRequireANonloopingPositiveDuration(bool zeroDuration)
        {
            var clip = Clip("Constant motion time face", 75);
            if (zeroDuration) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Face size"),
                new AnimationCurve(new Keyframe(0, 75)));
            var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            selected.motion = clip; selected.timeParameterActive = true; selected.timeParameter = "GestureRightWeight";
            controller.AddParameter("Custom face", AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine;
            var custom = State(machine, "Ordinary custom face", Clip("Ordinary custom face", 85));
            var transition = machine.defaultState.AddTransition(custom); transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.If, 0, "Custom face");
            controller.AddLayer("Constant clock support");
            var support = new AnimationClip { name = "Constant clock support" };
            AnimationUtility.SetEditorCurve(support, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.Blink"),
                zeroDuration ? new AnimationCurve(new Keyframe(0, 0)) : AnimationCurve.Constant(0, 1, 0));
            AssetDatabase.AddObjectToAsset(support, controller);
            var supportSettings = AnimationUtility.GetAnimationClipSettings(support); supportSettings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(support, supportSettings);
            var supportState = State(controller.layers[1].stateMachine, "Constant native clock", support);
            controller.layers[1].stateMachine.defaultState = supportState;
            supportState.timeParameterActive = true; supportState.timeParameter = "GestureLeftWeight";
            var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var metadata = Read();
            var entry = metadata.Entries.Single(item => item.Name.EndsWith(" / Constant motion time face", StringComparison.Ordinal));
            Assert.That(entry.Error, Is.Null, entry.Error); Assert.That(entry.Animation, Is.Empty);
            Assert.That(entry.Duration, Is.EqualTo(0)); Assert.That(entry.Loop, Is.False);
            Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight, Is.EqualTo(75).Within(.01));
            VrChatFxExpressions.Add(avatar, metadata);
            Assert.That(metadata.Entries.Any(item => item.Error == null && item.Values.Any(value => value.Shape == "Face size" && Mathf.Abs(value.Weight - 85) < .01f)), Is.True,
                "An irrelevant constant motion-time default must not cancel optional custom FX discovery.");
        }

        [Test]
        public void MotionTimeGestureUsesTheEffectiveOverrideClipLengthAndKeyBoundaries()
        {
            var original = MotionTimeClip("Original time", 2, 1, 10, 40, 85);
            var replacement = MotionTimeClip("Replacement time", 4, 1, 17, 38, 92);
            selected.motion = original; selected.timeParameterActive = true; selected.timeParameter = "GestureRightWeight";
            var runtime = new AnimatorOverrideController(controller); runtime[original] = replacement;
            try
            {
                var entries = Read(runtime).Entries;
                AssertNativeKnots(entries, new[] { HandInputs(0, 0), HandInputs(0, .25f), HandInputs(0, 1) }, runtime);
                Assert.That(entries.Select(entry => entry.Parameters["GestureRightWeight"]), Is.EquivalentTo(new[] { 0f, .25f, 1f }));
                foreach (var entry in entries)
                {
                    Assert.That(entry.Error, Is.Null, entry.Error); Assert.That(entry.Animation, Is.Empty);
                    Assert.That(entry.Name, Does.Contain(replacement.name));
                    Assert.That(entry.Values.Single(value => value.Shape == "Face size").Weight,
                        Is.EqualTo(Native(entry.Parameters, runtime).Weight).Within(.01));
                }
            }
            finally { Object.DestroyImmediate(runtime); }
        }

        [Test]
        public void OutOfRangeHandKnotsUseReachableNativeDomainBoundaries()
        {
            var tree = Tree("Boundary shaping", BlendTreeType.SimpleDirectional2D, "GestureLeftWeight", "GestureRightWeight");
            tree.AddChild(Clip("Negative shaping", 10), new Vector2(-.05f, -.05f));
            tree.AddChild(Clip("Middle left", 40), new Vector2(.5f, 0));
            tree.AddChild(Clip("Outer right", 85), new Vector2(0, 1.1f)); selected.motion = tree;
            var entries = Read().Entries;
            AssertNativeKnots(entries, new[] { HandInputs(0, 0), HandInputs(.5f, 0), HandInputs(0, 1) });
            foreach (var entry in entries)
            {
                AssertNative(entry);
                Assert.That(entry.Parameters.Where(pair => pair.Key.EndsWith("Weight", StringComparison.Ordinal)).All(pair => pair.Value >= 0 && pair.Value <= 1), Is.True);
                Assert.That(entry.Name, Does.Not.Contain("-0.05"));
            }
        }

        [TestCase("undeclared")]
        [TestCase("writer")]
        [TestCase("loop")]
        [TestCase("speed")]
        [TestCase("cycle")]
        [TestCase("mirror")]
        public void MotionTimeCannotBypassUnsafeClockInputsOrAmbiguousStateControls(string damage)
        {
            var clip = MotionTimeClip("Unsafe motion time", 2, .8f, 10, 45, 85);
            selected.motion = clip; selected.timeParameterActive = true; selected.timeParameter = "GestureRightWeight";
            if (damage == "undeclared") selected.timeParameter = "Missing clock";
            if (damage == "writer")
            {
                controller.AddLayer("Clock producer"); var machine = controller.layers[1].stateMachine;
                var producer = new AnimationClip { name = "Clock writer" };
                AnimationUtility.SetEditorCurve(producer, EditorCurveBinding.FloatCurve("", typeof(Animator), "GestureRightWeight"), AnimationCurve.Constant(0, 1, 1));
                AssetDatabase.AddObjectToAsset(producer, controller); machine.defaultState = State(machine, "Producer", producer);
            }
            if (damage == "loop")
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime = true; AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            if (damage == "speed") selected.speed = .5f;
            if (damage == "cycle") selected.cycleOffset = .25f;
            if (damage == "mirror") selected.mirror = true;
            var entries = Read().Entries;
            Assert.That(entries, Is.Not.Empty);
            Assert.That(entries.All(entry => entry.Error != null && entry.Values.Count == 0 && entry.Animation.Count == 0), Is.True);
        }
    }
}
