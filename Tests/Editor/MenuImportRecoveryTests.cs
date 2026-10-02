using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    // These are SDK-shaped serialized fields, read by the production traversal.
    // SDK/Animator/export integration is covered separately below, not simulated
    // by inventing a VRCAvatarDescriptor with the SDK's full type name.
    public sealed class MenuImportRecoveryTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(255)]
        [TestCase(256)]
        public void EveryLeafUpToTheLimitIsReadWithoutImplicitTruncation(int count)
        {
            var menu = Leaves(count);
            var result = VrChatExpressionMenu.ReadMenu(menu);
            Assert.That(result.Entries.Count, Is.EqualTo(count));
            Assert.That(menu.controls.Count, Is.EqualTo(count));
            if (count > 0) Assert.That(result.Entries.Last().Name, Is.EqualTo("Leaf " + (count - 1)));
        }

        [Test]
        public void EmptyAndNullSubmenusAfter256CandidatesDoNotCauseFalseOverflow()
        {
            var menu = Leaves(256);
            menu.controls.Add(Branch("Empty", new Menu()));
            menu.controls.Add(Branch("Null", null));
            Assert.That(VrChatExpressionMenu.ReadMenu(menu).Entries.Count, Is.EqualTo(256));
        }

        [Test]
        public void The257thLeafProducesACompleteTypedCountAndNoSplitMenuAdvice()
        {
            var menu = Leaves(257);
            var error = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu));
            Assert.That(error.Summary.Root, Is.SameAs(menu));
            Assert.That(error.Summary.CandidateCount, Is.EqualTo(257));
            Assert.That(error.Summary.IsComplete, Is.True);
            Assert.That(error.Message, Does.Not.Contain("メニューを分割"));
            Assert.That(menu.controls.Count, Is.EqualTo(257));
        }

        [Test]
        public void SplittingIntoEightControlPagesStillReportsTheTotalAndAllowsBranchScope()
        {
            var menu = EightControlTree(257);
            Assert.That(AllMenus(menu).All(m => m.controls.Count <= 8), Is.True, "Each SDK-shaped menu respects the individual eight-control limit.");
            var error = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu));
            Assert.That(error.Summary.CandidateCount, Is.EqualTo(257));
            Assert.That(error.Summary.IsComplete, Is.True);
            Assert.That(error.Summary.Branches.Any(b => b.Path == "/0"), Is.True);
            var policy = Policy(menu, "/0");
            var scoped = VrChatExpressionMenu.ReadMenu(menu, policy);
            Assert.That(scoped.Entries.Count, Is.LessThanOrEqualTo(256));
            Assert.That(scoped.Entries.Count, Is.GreaterThan(0));
            Assert.That(scoped.Entries.All(e => !e.Id.StartsWith("/0/", StringComparison.Ordinal)), Is.True);
            Assert.That(AllMenus(menu).Sum(m => m.controls.Count(c => c.type != "SubMenu")), Is.EqualTo(257));
            Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu), "Clearing the scope reads the full source again.");
        }

        [TestCase("RadialPuppet", true)]
        [TestCase("TwoAxisPuppet", true)]
        [TestCase("FourAxisPuppet", true)]
        [TestCase("Toggle", false)]
        [TestCase("Button", false)]
        public void UnsupportedPuppetsAndMissingParametersAreStillExplicitCandidates(string type, bool puppet)
        {
            var menu = Leaves(255);
            menu.controls.Add(new Control { name = "Unsupported", type = type });
            var entries = VrChatExpressionMenu.ReadMenu(menu).Entries;
            Assert.That(entries.Count, Is.EqualTo(256));
            Assert.That(entries.Last().Error, puppet ? Does.Contain("Puppet") : Does.Contain("パラメーター"));
            menu.controls.Add(new Control { name = "Another unsupported", type = type });
            Assert.That(Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu)).Summary.CandidateCount, Is.EqualTo(257));
        }

        [Test]
        public void SharedMenuThroughFivePathsKeepsIndependentGatesAndCountsEveryRoute()
        {
            var shared = Leaves(52);
            var menu = new Menu();
            for (var i = 0; i < 5; i++) menu.controls.Add(Branch("Shared", shared, "Gate" + i, i + 1));
            var limit = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu));
            Assert.That(limit.Summary.CandidateCount, Is.EqualTo(260));
            var result = VrChatExpressionMenu.ReadMenu(menu, Policy(menu, "/4"));
            Assert.That(result.Entries.Count, Is.EqualTo(208));
            for (var i = 0; i < 4; i++)
            {
                var entries = result.Entries.Where(e => e.Id.StartsWith("/" + i + "/", StringComparison.Ordinal)).ToArray();
                Assert.That(entries.Length, Is.EqualTo(52));
                Assert.That(entries.All(e => e.Parameters["Gate" + i] == i + 1 && e.Parameters.Count == 2), Is.True,
                    "A shared asset's other parent gates must not leak into this route.");
            }
            Assert.That(shared.controls.Count, Is.EqualTo(52));
            Assert.That(menu.controls.Count, Is.EqualTo(5));
        }

        [Test]
        public void DuplicateBranchLabelsAreExcludedBySiblingIndexAndNotByName()
        {
            var menu = new Menu();
            menu.controls.Add(Branch("Same name", Leaves(129), "FirstGate", 1));
            menu.controls.Add(Branch("Same name", Leaves(129), "SecondGate", 2));
            var limit = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu));
            Assert.That(limit.Summary.Branches.Any(b => b.Path == "/0"), Is.True);
            Assert.That(limit.Summary.Branches.Any(b => b.Path == "/1"), Is.True);
            var result = VrChatExpressionMenu.ReadMenu(menu, Policy(menu, "/0"));
            Assert.That(result.Entries.Count, Is.EqualTo(129));
            Assert.That(result.Entries.All(e => e.Id.StartsWith("/1/", StringComparison.Ordinal)), Is.True);
            Assert.That(result.Entries.All(e => e.Parameters["SecondGate"] == 2 && !e.Parameters.ContainsKey("FirstGate")), Is.True);
        }

        [Test]
        public void SelectedNestedScopePreservesEveryAncestorParameterGate()
        {
            var nested = new Menu();
            nested.controls.Add(Branch("Faces", Leaves(2), "FaceGate", 4));
            nested.controls.Add(Branch("Clothes", Leaves(256), "ClothesGate", 5));
            var menu = new Menu(); menu.controls.Add(Branch("Outer", nested, "OuterGate", 3));
            Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu));
            var result = VrChatExpressionMenu.ReadMenu(menu, Policy(menu, "/0/1"));
            Assert.That(result.Entries.Count, Is.EqualTo(2));
            Assert.That(result.Entries.All(e => e.Parameters["OuterGate"] == 3 && e.Parameters["FaceGate"] == 4 && !e.Parameters.ContainsKey("ClothesGate")), Is.True);
        }

        [Test]
        public void ABranchPolicyCannotSilentlyApplyToAReplacedRoot()
        {
            var menu = new Menu(); menu.controls.Add(Branch("Scope", Leaves(257)));
            var replacement = new Menu(); replacement.controls.Add(Branch("Scope", Leaves(1)));
            Assert.Throws<VrChatMenuImportPolicyException>(() => VrChatExpressionMenu.ReadMenu(replacement, Policy(menu, "/0")));
        }

        [TestCase("/999")]
        [TestCase("/0/999")]
        [TestCase("/0/0")]
        [TestCase("/Same name")]
        [TestCase("/00")]
        [TestCase("/-1")]
        [TestCase("/0/")]
        public void ForgedOrRemovedBranchRoutesAreRejectedInsteadOfBeingIgnored(string path)
        {
            var menu = new Menu(); menu.controls.Add(Branch("Same name", Leaves(1)));
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionMenu.ReadMenu(menu, Policy(menu, path)));
        }

        [Test]
        public void ExplicitWholeMenuOmissionCanBeClearedWithoutEditingAnySourceControls()
        {
            var menu = EightControlTree(257);
            var before = AllMenus(menu).Select(m => m.controls.ToArray()).ToArray();
            var policy = new VrChatMenuImportPolicy { ExpectedRoot = menu, SkipAll = true };
            Assert.That(VrChatExpressionMenu.ReadMenu(menu, policy).Entries, Is.Empty);
            Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu, new VrChatMenuImportPolicy()));
            var after = AllMenus(menu).Select(m => m.controls.ToArray()).ToArray();
            Assert.That(after.Length, Is.EqualTo(before.Length));
            for (var i = 0; i < before.Length; i++) Assert.That(after[i], Is.EqualTo(before[i]));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DiscoveryHasItsOwnVisitBudgetAndLabelsCountsAsIncomplete(bool menus)
        {
            var menu = new Menu(); var shared = menus ? new Menu() : null;
            var count = menus ? VrChatExpressionMenu.MaximumMenuVisits : VrChatExpressionMenu.MaximumControlVisits + 1;
            for (var i = 0; i < count; i++) menu.controls.Add(Branch("Empty " + i, shared));
            var error = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(menu));
            Assert.That(error.Summary.CandidateCount, Is.Zero);
            Assert.That(error.Summary.IsComplete, Is.False, "A bounded scan must never present its partial count as an exact total.");
            Assert.That(error.Summary.LimitReason, Is.EqualTo(menus ? "menu-visits" : "control-visits"));
            Assert.That(VrChatExpressionMenu.ReadMenu(menu, new VrChatMenuImportPolicy { SkipAll = true }).Entries, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void IncompleteDeepOrCyclicBranchDoesNotStopHealthySiblingDiscovery(bool deep)
        {
            Menu incomplete;
            if (deep)
            {
                incomplete = Leaves(3);
                for (var i = 0; i < VrChatExpressionMenu.MaximumDepth; i++)
                { var wrapper = new Menu(); wrapper.controls.Add(Branch("Deep", incomplete)); incomplete = wrapper; }
            }
            else
            {
                incomplete = Leaves(1); incomplete.controls.Add(Branch("Loop", incomplete));
            }
            var problem = new Menu();
            problem.controls.Add(Branch("Incomplete", incomplete));
            problem.controls.Add(Branch("Healthy inner sibling", Leaves(2)));
            var root = new Menu(); root.controls.Add(Branch("Problem container", problem));
            root.controls.Add(Branch("Healthy outer sibling", EightControlTree(257)));
            var expected = deep ? 259 : 260;
            var summary = VrChatExpressionMenu.Discover(root);
            Assert.That(summary.IsComplete, Is.False);
            Assert.That(summary.BudgetStopped, Is.False, "An incomplete reference must not terminate independent sibling inspection.");
            Assert.That(summary.LimitReason, Is.EqualTo(deep ? "depth" : "cycle"));
            Assert.That(summary.CandidateCount, Is.EqualTo(expected));
            var ancestor = summary.Branches.Single(b => b.Path == "/0");
            Assert.That(ancestor.IsComplete, Is.False); Assert.That(ancestor.CandidateCount, Is.EqualTo(deep ? 2 : 3));
            var incompleteBranch = summary.Branches.Single(b => b.Path == "/0/0");
            Assert.That(incompleteBranch.IsComplete, Is.False);
            var inner = summary.Branches.Single(b => b.Path == "/0/1");
            Assert.That(inner.IsComplete, Is.True); Assert.That(inner.CandidateCount, Is.EqualTo(2));
            var outer = summary.Branches.Single(b => b.Path == "/1");
            Assert.That(outer.IsComplete, Is.True); Assert.That(outer.CandidateCount, Is.EqualTo(257));
            var error = Assert.Throws<VrChatMenuImportLimitException>(() => VrChatExpressionMenu.ReadMenu(root));
            Assert.That(error.Summary.IsComplete, Is.False); Assert.That(error.Summary.CandidateCount, Is.EqualTo(expected));
            Assert.That(error.Summary.Branches.Single(b => b.Path == "/1").IsComplete, Is.True);
            var report = ExportRecoveryReport.FromException(null, error);
            Assert.That(report.Diagnostics.Single().Reason, Does.Not.Contain("512"), "Depth/cycle truncation must not claim that a menu-visit budget was reached.");
            Assert.That(report.Diagnostics.Single().Reason, Does.Not.Contain("8192"));
            var remaining = VrChatExpressionMenu.ReadMenu(root, Policy(root, "/1"));
            Assert.That(remaining.Entries.Count, Is.EqualTo(deep ? 2 : 3));
            var onlyProblem = new Menu(); onlyProblem.controls.Add(root.controls[0]);
            Assert.That(VrChatExpressionMenu.ReadMenu(onlyProblem).Messages, Is.Not.Empty,
                "The existing nonfatal depth/cycle omission warning is retained without relying on an omission-policy message.");
        }

        [TestCase(86)]
        [TestCase(100000)]
        public void ForgedLongRoutesAreRejectedBeforeInspectingSerializedMenuControls(int length)
        {
            var menu = new GuardedMenu(); var route = new string('/', length);
            Assert.That(VrChatExpressionMenu.IsSubMenuPath(menu, route), Is.False);
            var policy = new VrChatMenuImportPolicy { ExpectedRoot = menu }; policy.ExcludedBranches.Add(route);
            Assert.Throws<InvalidOperationException>(() => VrChatExpressionMenu.ReadMenu(menu, policy));
            Assert.That(menu.ReadCount, Is.Zero, "A hostile route cannot force menu enumeration or reflection before the length guard.");
        }

        [Test]
        public void WholeMenuOmissionRemainsExplicitAndConsistentWhenPreparationReplacesTheRoot()
        {
            var original = EightControlTree(257); var prepared = EightControlTree(300);
            var policy = Policy(original, "/999"); policy.SkipAll = true;
            Assert.That(VrChatExpressionMenu.ReadMenu(original, policy).Entries, Is.Empty);
            Assert.That(VrChatExpressionMenu.ReadMenu(prepared, policy).Entries, Is.Empty,
                "Whole-menu consent is independent of a build-generated branch identity.");
            policy.SkipAll = false;
            Assert.Throws<VrChatMenuImportPolicyException>(() => VrChatExpressionMenu.ReadMenu(prepared, policy),
                "Branch-only consent must never move to a replacement root with a coincidentally matching route.");
        }

        [Test]
        public void WholeMenuConsentCannotBypassTheOriginalAvatarOwnerGuard()
        {
            var source = new GameObject("Original avatar"); var foreign = new GameObject("Foreign avatar");
            try
            {
                // SDK-free avatars have a null source menu. The exporter still
                // supports whole-menu consent for a menu generated later by a
                // preparation plugin, while rejecting another avatar's recipe.
                var options = new ExportRecoveryOptions();
                options.Actions.Add(new ExportRecoveryAction { Id = "skip", Kind = ExportRecoveryActionKind.SkipVrChatMenus, MenuOwner = source });
                options.Actions.Add(new ExportRecoveryAction { Id = "scope", Kind = ExportRecoveryActionKind.ExcludeMenuBranch, MenuOwner = source, MenuPath = "/999" });
                Assert.That(ExportRecoveryReport.MenuImportPolicy(source, options).SkipAll, Is.True,
                    "Whole-menu consent wins over same-source branch paths that preparation cannot preserve.");
                options.Actions[1].MenuOwner = foreign;
                Assert.Throws<InvalidOperationException>(() => ExportRecoveryReport.MenuImportPolicy(source, options));
            }
            finally { UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(foreign); }
        }

        [Test]
        public void CopyWarningsDescribeOnlyWholeMenuOmissionWhenItSupersedesBranchChoices()
        {
            using var f = new ExportRecoveryTests.RecoveryFixture();
            var options = new ExportRecoveryOptions();
            options.Actions.Add(new ExportRecoveryAction { Id = "skip", Kind = ExportRecoveryActionKind.SkipVrChatMenus, MenuOwner = f.Source });
            options.Actions.Add(new ExportRecoveryAction { Id = "branch", Kind = ExportRecoveryActionKind.ExcludeMenuBranch, MenuOwner = f.Source, MenuPath = "/999" });
            var copy = UnityEngine.Object.Instantiate(f.Source); var owned = new List<Material>(); var warnings = new List<string>();
            var foreign = new GameObject("Foreign recipe owner");
            try
            {
                var session = new ExportRecoveryCopySession(f.Source, copy, owned, options);
                session.Apply(warnings);
                Assert.That(warnings.Count, Is.EqualTo(1));
                Assert.That(warnings.Single(), Does.Contain(nameof(ExportRecoveryActionKind.SkipVrChatMenus)));
                Assert.That(warnings.Single(), Does.Not.Contain(nameof(ExportRecoveryActionKind.ExcludeMenuBranch)));
                Assert.That(options.Actions.Count, Is.EqualTo(2), "The superseded branch choice remains available when full omission is deselected.");
                Assert.That(f.Renderer.sharedMaterial, Is.SameAs(f.Material));
                Assert.That(copy.GetComponentInChildren<Renderer>().sharedMaterial, Is.Not.SameAs(f.Material));
                options.Actions[1].MenuOwner = foreign;
                Assert.Throws<InvalidOperationException>(() => session.Apply(warnings), "Whole omission must still reject a foreign branch recipe before changing its copy.");
                Assert.That(warnings.Count, Is.EqualTo(1)); Assert.That(owned.Count, Is.EqualTo(1));
                Assert.That(f.Renderer.sharedMaterial, Is.SameAs(f.Material));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy); UnityEngine.Object.DestroyImmediate(foreign);
                foreach (var material in owned) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SuccessfulResultKeepsAnIndependentScopeAndActionSnapshot()
        {
            var source = new GameObject("Snapshot source");
            try
            {
                var action = new ExportRecoveryAction { Id = "branch-id", Kind = ExportRecoveryActionKind.ExcludeMenuBranch, MenuOwner = source, MenuPath = "/1" };
                var options = new ExportRecoveryOptions(); options.Actions.Add(action);
                var diagnostic = new ExportRecoveryDiagnostic { Id = "diagnostic-id", Code = "menu-import-limit", Stage = "表情メニュー読込",
                    Target = "Original scope label [/1]", Reason = "Original reason", Remedy = "Original remedy", LostEffect = "Original lost effect", Action = action };
                var result = new ExportRecoverySession.SuccessfulAttempt(new byte[] { 1 }, new List<string>(), options,
                    ExportRecoverySourceStamp.Capture(source), new[] { diagnostic });
                action.Id = "changed-id"; action.MenuPath = "/99"; action.MenuOwner = null; action.Kind = ExportRecoveryActionKind.SkipVrChatMenus;
                diagnostic.Target = "Changed label"; diagnostic.LostEffect = "Changed effect"; options.Actions.Clear();
                var captured = result.Options.Actions.Single();
                Assert.That(captured.Id, Is.EqualTo("branch-id")); Assert.That(captured.MenuPath, Is.EqualTo("/1"));
                Assert.That(captured.Kind, Is.EqualTo(ExportRecoveryActionKind.ExcludeMenuBranch)); Assert.That(captured.MenuOwner, Is.SameAs(source));
                Assert.That(captured, Is.Not.SameAs(action));
                Assert.That(result.Diagnostics.Single().Action, Is.SameAs(captured));
                Assert.That(result.Diagnostics.Single().Target, Is.EqualTo("Original scope label [/1]"));
                Assert.That(result.Diagnostics.Single().LostEffect, Is.EqualTo("Original lost effect"));
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        private static VrChatMenuImportPolicy Policy(Menu root, params string[] paths)
        { var policy = new VrChatMenuImportPolicy { ExpectedRoot = root }; foreach (var path in paths) policy.ExcludedBranches.Add(path); return policy; }
        private static Menu Leaves(int count)
        {
            var menu = new Menu();
            for (var i = 0; i < count; i++) menu.controls.Add(new Control { name = "Leaf " + i, type = "Toggle", parameter = new Parameter { name = "Face" }, value = i });
            return menu;
        }
        private static Control Branch(string name, Menu menu, string parameter = null, float value = 0)
            => new Control { name = name, type = "SubMenu", subMenu = menu, parameter = parameter == null ? null : new Parameter { name = parameter }, value = value };
        private static Menu EightControlTree(int count)
        {
            if (count <= 8) return Leaves(count);
            var menu = new Menu(); var remaining = count;
            while (remaining > 0)
            {
                var group = Math.Min(count <= 64 ? 8 : 64, remaining);
                menu.controls.Add(Branch("Page " + menu.controls.Count, EightControlTree(group)));
                remaining -= group;
            }
            return menu;
        }
        private static IEnumerable<Menu> AllMenus(Menu root)
        { yield return root; foreach (var control in root.controls.Where(c => c.type == "SubMenu" && c.subMenu != null)) foreach (var child in AllMenus(control.subMenu)) yield return child; }
        private sealed class Menu { public List<Control> controls = new List<Control>(); }
        private sealed class GuardedMenu
        {
            internal int ReadCount;
            public IEnumerable<object> controls { get { ReadCount++; throw new Exception("Unexpected serialized menu traversal."); } }
        }
        private sealed class Parameter { public string name; }
        private sealed class Control { public string name, type; public Parameter parameter; public float value; public Menu subMenu; }
    }
}
