import importlib.util
import json
import math
import re
from pathlib import Path
import unittest
from unittest import mock
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('upstream', Path(__file__).with_name('check-upstream-dependencies.py'))
upstream = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upstream)
spec = importlib.util.spec_from_file_location('unity_runner', Path(__file__).with_name('run-unity-compatibility.py'))
unity_runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unity_runner)


class UnityResultTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.xml = Path(self.temp.name) / 'result.xml'
        self.root = ET.Element('test-run', result='Passed')
        methods = {
            'DependencyEnvironmentTests': ('ActualInstalledPackagesMatchRequestedTestEnvironment', 1),
            'DependencyStartupTests': ('MenuResolvesBackendWithoutInitializationRegistration', 1),
            'DependencyRoundTripTests': ('SupportedBackendPreservesMeshesMorphsAndMaterialBindingsOnReimport', 2),
            'RendererSelectionTests': ('RendererCase', 4),
            'SkinnedMeshFallbackWeightTests': ('SkinCase', 12),
        }
        for suite, (method, count) in methods.items():
            name = 'VRVlog.LilToonExporter.Tests.' + suite
            for index in range(count):
                ET.SubElement(self.root, 'test-case', classname=name, fullname=name + '.' + method + '(' + str(index) + ')', result='Passed')

    def check(self):
        ET.ElementTree(self.root).write(self.xml)
        return unity_runner.validate_result(self.xml, True)

    def test_all_required_suites_pass(self):
        self.assertEqual(len(self.check()), 20)

    def test_no_suite_can_disappear_from_a_passing_result(self):
        for name in ['DependencyEnvironmentTests', 'DependencyStartupTests', 'DependencyRoundTripTests', 'RendererSelectionTests', 'SkinnedMeshFallbackWeightTests']:
            with self.subTest(suite=name):
                removed = [c for c in self.root if c.get('classname').endswith('.' + name)]
                for case in removed: self.root.remove(case)
                with self.assertRaises(SystemExit): self.check()
                for case in removed: self.root.append(case)

    def test_one_missing_skin_case_is_not_success(self):
        self.root.remove(self.root[-1])
        with self.assertRaises(SystemExit): self.check()

    def test_skipped_or_failed_case_is_not_success(self):
        for result in ['Skipped', 'Failed']:
            self.root[-1].set('result', result)
            with self.assertRaises(SystemExit): self.check()

    def test_missing_result_is_not_success(self):
        with self.assertRaises(FileNotFoundError): unity_runner.validate_result(self.xml, True)


class ExporterProfileResultTests(unittest.TestCase):
    # These XML fixtures test the gate, not the Unity exporter or installed SDKs.
    def setUp(self):
        UnityResultTests.setUp(self)
        self.profile = 'exporter-integration'
        methods_by_suite = {suite: dict(methods) for suite, methods in unity_runner.BEHAVIOR_CASES.items()}
        for suite, methods in unity_runner.INTEGRATION_CASES.items():
            methods_by_suite.setdefault(suite, {}).update(methods)
        for suite, methods in methods_by_suite.items():
            name = unity_runner.NAMESPACE + suite
            for method, count in methods.items():
                variants = unity_runner.INTEGRATION_VARIANTS.get(suite, {}).get(method)
                for index in range(count):
                    ET.SubElement(self.root, 'test-case', classname=name,
                                  fullname=name + '.' + method + '(' + (variants[index] if variants else str(index)) + ')', result='Passed')

    def check(self):
        ET.ElementTree(self.root).write(self.xml)
        return unity_runner.validate_result(self.xml, True, self.profile)

    def remove(self, suite, method=None):
        name = unity_runner.NAMESPACE + suite
        removed = [c for c in self.root if c.get('classname') == name and
                   (method is None or c.get('fullname').startswith(name + '.' + method + '('))]
        for case in removed: self.root.remove(case)
        return removed

    def test_all_required_suites_pass(self):
        expected = 20 + sum(sum(methods.values()) for methods in
                            [*unity_runner.BEHAVIOR_CASES.values(), *unity_runner.INTEGRATION_CASES.values()])
        self.assertEqual(len(self.check()), expected)

    def test_no_regression_class_can_disappear(self):
        for suite in [*unity_runner.BEHAVIOR_CASES, *unity_runner.INTEGRATION_CASES]:
            with self.subTest(suite=suite):
                removed = self.remove(suite)
                with self.assertRaises(SystemExit): self.check()
                self.root.extend(removed)

    def test_no_required_regression_or_parameter_variant_can_disappear(self):
        required = unity_runner.required_regressions(True, self.profile)
        for suite, methods in required.items():
            for method in methods:
                with self.subTest(suite=suite, method=method):
                    removed = self.remove(suite, method)
                    with self.assertRaises(SystemExit): self.check()
                    self.root.extend(removed)
                    if len(removed) > 1:
                        self.root.remove(removed[-1])
                        with self.assertRaises(SystemExit): self.check()
                        self.root.append(removed[-1])

    def test_only_behavior_does_not_claim_installed_aao_integration(self):
        for suite, methods in unity_runner.INTEGRATION_CASES.items():
            for method in methods: self.remove(suite, method)
        self.profile = 'exporter-behavior'
        self.assertGreater(len(self.check()), 20)
        self.profile = 'exporter-integration'
        with self.assertRaises(SystemExit): self.check()

    def test_shared_physbone_suite_selects_behavior_without_claiming_optional_integration(self):
        behavior = unity_runner.profile_filters(True, 'exporter-behavior')
        name = unity_runner.NAMESPACE + 'PhysBoneSpringExportTests'
        self.assertNotIn(name, behavior)
        self.assertIn(name + '.ActualExportImportsNestedOwnersWithoutSharedJointsOrChangedAppearance', behavior)
        self.assertNotIn(name + '.InstalledAvatarOptimizerPreservesSpringMotionAndCollisionsAfterFullExport', behavior)
        self.assertIn(name, unity_runner.profile_filters(True, 'exporter-integration'))
        required = unity_runner.required_regressions(True, 'exporter-integration')['PhysBoneSpringExportTests']
        self.assertIn('ActualExportImportsNestedOwnersWithoutSharedJointsOrChangedAppearance', required)
        self.assertIn('InstalledAvatarOptimizerPreservesSpringMotionAndCollisionsAfterFullExport', required)

    def test_synthetic_mesh_replacement_cannot_replace_official_mesh_deleter_cases(self):
        cases = self.remove('MeshDeleterIntegrationTests', 'DeletedSharedMeshAndMorphSurviveOneClickExportAndReimport')
        self.assertEqual(len(cases), 4)
        self.root.extend(cases[:2])
        # Missing installed-tool variants cannot be covered by the two passing
        # synthetic mesh replacements, even with a Passed XML root.
        with self.assertRaises(SystemExit): self.check()
        self.root.extend(cases[2:])
        self.check()
        for result in ('Skipped', 'Failed', 'Inconclusive'):
            with self.subTest(result=result):
                cases[-1].set('result', result)
                with self.assertRaises(SystemExit): self.check()
        cases[-1].set('result', 'Passed')

    def test_installed_ma_appearance_cannot_disappear_or_skip(self):
        method = 'InstalledMeshCutterPreservesMorphsAndSourceMesh'
        cases = self.remove('AppearancePreparationTests', method)
        self.assertEqual(len(cases), 2)
        with self.assertRaises(SystemExit): self.check()
        self.root.extend(cases)
        cases[-1].set('result', 'Skipped')
        with self.assertRaises(SystemExit): self.check()

    def test_preview_disabled_deletion_variants_cannot_disappear_or_skip(self):
        method = 'InstalledDeletionSurvivesOneClickAndLiveMorphsRegardlessOfPreviewSettings'
        cases = self.remove('MaDeletionExportTests', method)
        self.assertEqual(len(cases), 6)
        self.root.extend(cases[:2])
        with self.assertRaises(SystemExit): self.check()
        self.root.extend(cases[2:])
        self.check()
        cases[-1].set('result', 'Skipped')
        with self.assertRaises(SystemExit): self.check()

    def test_other_parameter_values_cannot_replace_official_or_preview_disabled_cases(self):
        for suite, method in (
            ('MeshDeleterIntegrationTests', 'DeletedSharedMeshAndMorphSurviveOneClickExportAndReimport'),
            ('MaDeletionExportTests', 'InstalledDeletionSurvivesOneClickAndLiveMorphsRegardlessOfPreviewSettings'),
            ('MaPermanentAppearanceExportTests', 'MergedPermanentPupilHideSurvivesVrmReloadBlinkAndMenuGeometry'),
        ):
            with self.subTest(suite=suite):
                cases = self.remove(suite, method)
                original = cases[-1].get('fullname')
                cases[-1].set('fullname', unity_runner.NAMESPACE + suite + '.' + method + '(True,99)')
                self.root.extend(cases)
                with self.assertRaises(SystemExit): self.check()
                cases[-1].set('fullname', original)
                self.check()

    def test_additive_case_cannot_be_replaced_by_duplicate_override_case(self):
        suite = 'MergedFxDefaultsTests'
        method = 'FractionalStationaryFxGeometrySurvivesOneClickExportAndVrmReimport'
        cases = self.remove(suite, method)
        cases[1].set('fullname', cases[0].get('fullname'))
        self.root.extend(cases)
        with self.assertRaises(SystemExit): self.check()

    def test_similarly_named_method_cannot_satisfy_the_required_case(self):
        case = self.root[-1]
        case.set('fullname', case.get('fullname').replace('(', 'Unexpected('))
        with self.assertRaises(SystemExit): self.check()

    def test_unlisted_case_in_selected_class_must_also_pass(self):
        name = unity_runner.NAMESPACE + 'NeutralShapeSamplerTests'
        case = ET.SubElement(self.root, 'test-case', classname=name, fullname=name + '.AdditionalRegression', result='Passed')
        self.check()
        for result in ['Skipped', 'Failed', 'Inconclusive']:
            with self.subTest(result=result):
                case.set('result', result)
                with self.assertRaises(SystemExit): self.check()

    def test_skipped_or_failed_required_case_is_not_success(self):
        for result in ['Skipped', 'Failed', 'Inconclusive']:
            with self.subTest(result=result):
                self.root[-1].set('result', result)
                with self.assertRaises(SystemExit): self.check()

    def test_unselected_class_is_rejected(self):
        name = unity_runner.NAMESPACE + 'UnrequestedTests'
        ET.SubElement(self.root, 'test-case', classname=name, fullname=name + '.Unexpected', result='Passed')
        with self.assertRaises(SystemExit): self.check()

    def test_root_failure_is_not_hidden_by_passing_cases(self):
        self.root.set('result', 'Failed')
        with self.assertRaises(SystemExit): self.check()

    def test_missing_identity_and_wrong_class_are_rejected(self):
        for field, value in [('fullname', ''), ('classname', unity_runner.NAMESPACE + 'WrongTests')]:
            with self.subTest(field=field):
                case = self.root[-1]
                original = case.get(field)
                case.set(field, value)
                with self.assertRaises(SystemExit): self.check()
                case.set(field, original)

    def test_malformed_xml_is_not_success(self):
        self.xml.write_text('<test-run result="Passed"><test-case', encoding='utf-8')
        with self.assertRaises(ET.ParseError): unity_runner.validate_result(self.xml, True, self.profile)


class UnityProfilePolicyTests(unittest.TestCase):
    def test_default_filter_set_is_unchanged(self):
        self.assertEqual(unity_runner.profile_filters(True, 'compatibility'), [
            unity_runner.NAMESPACE + suite for suite in (
                'DependencyEnvironmentTests', 'DependencyStartupTests', 'DependencyRoundTripTests',
                'RendererSelectionTests', 'SkinnedMeshFallbackWeightTests')])
        self.assertEqual(len(unity_runner.profile_filters(False, 'compatibility')), 2)

    def test_missing_or_unsupported_dependencies_cannot_start_behavior_profiles(self):
        for profile in ['exporter-behavior', 'exporter-integration', 'unknown']:
            with self.subTest(profile=profile):
                with self.assertRaises(SystemExit): unity_runner.profile_filters(False, profile)

    def test_whole_required_classes_are_selected(self):
        filters = unity_runner.profile_filters(True, 'exporter-integration')
        self.assertEqual(len(filters), len(set(filters)))
        for suite in [*unity_runner.BEHAVIOR_CASES, *unity_runner.INTEGRATION_CASES]:
            self.assertIn(unity_runner.NAMESPACE + suite, filters)
        # Additional-playable proof is an exporter behavior requirement in both
        # profiles; it cannot move behind an optional integration-only gate.
        for profile in ('exporter-behavior', 'exporter-integration'):
            for suite, variants in (('AdditionalPlayableCallbackTests', 53), ('TemporalNeutralShapeTests', 23), ('NeutralLayerControlTests', 48), ('SelectedLayerControlTests', 40), ('SelectedExpressionAppearanceTests', 61)):
                self.assertIn(unity_runner.NAMESPACE + suite,
                              unity_runner.profile_filters(True, profile))
                self.assertEqual(sum(unity_runner.required_regressions(True, profile)[suite].values()), variants)

    def test_mesh_deleter_and_ma_appearance_are_explicit_integration_requirements(self):
        behavior = unity_runner.profile_filters(True, 'exporter-behavior')
        integration = unity_runner.profile_filters(True, 'exporter-integration')
        for suite in ('MeshDeleterIntegrationTests', 'MaDeletionExportTests', 'MaPermanentAppearanceExportTests', 'AppearancePreparationTests'):
            self.assertNotIn(unity_runner.NAMESPACE + suite, behavior)
            self.assertIn(unity_runner.NAMESPACE + suite, integration)
        required = unity_runner.required_regressions(True, 'exporter-integration')
        self.assertEqual(required['MeshDeleterIntegrationTests'], {
            'DeletedSharedMeshAndMorphSurviveOneClickExportAndReimport': 4})
        self.assertEqual(sum(required['AppearancePreparationTests'].values()), 12)
        self.assertEqual(required['MaDeletionExportTests'], {
            'InstalledDeletionSurvivesOneClickAndLiveMorphsRegardlessOfPreviewSettings': 6})
        self.assertEqual(required['MaPermanentAppearanceExportTests'], {
            'MergedPermanentPupilHideSurvivesVrmReloadBlinkAndMenuGeometry': 4})

    def test_required_case_names_and_counts_match_checked_in_fixtures(self):
        methods_by_suite = {suite: dict(methods) for suite, methods in unity_runner.BEHAVIOR_CASES.items()}
        for suite, methods in unity_runner.INTEGRATION_CASES.items():
            methods_by_suite.setdefault(suite, {}).update(methods)
        for suite, methods in methods_by_suite.items():
            source = (ROOT / 'Tests/Editor' / (suite + '.cs')).read_text(encoding='utf-8-sig')
            # Line-bounded attributes avoid ambiguous nested whitespace repeats
            # when scanning a long class for a later method.
            declarations = dict((method, attributes) for attributes, method in re.findall(
                r'((?:^[ \t]*\[(?:Test|UnityTest|TestCase\([^\r\n]*\)|TestCaseSource\([^\r\n]*\))\][ \t]*(?:\r?\n)?)+)'
                r'[ \t]*public[ \t]+(?:async[ \t]+)?(?:void|Task|IEnumerator)[ \t]+(\w+)\(', source, re.MULTILINE))
            if suite not in unity_runner.INTEGRATION_CASES:
                self.assertEqual(set(methods), set(declarations),
                                 'Whole-class filters must pin every checked-in regression, not only a required minimum')
            for method, count in methods.items():
                with self.subTest(suite=suite, method=method):
                    self.assertIn(method, declarations, 'Required regression must remain a real checked-in test')
                    sources = re.findall(r'\[TestCaseSource\(nameof\((\w+)\)\)\]', declarations[method])
                    if sources:
                        self.assertEqual(len(sources), 1, 'A source case must have one auditable generator')
                        generator = re.search(r'private static IEnumerable<TestCaseData> ' + re.escape(sources[0]) +
                                              r'\(\)\s*\{(.*?)^        \}', source, re.MULTILINE | re.DOTALL)
                        self.assertIsNotNone(generator, 'The native matrix generator must remain checked in')
                        dimensions = re.findall(r'foreach\s*\(var \w+ in new\[\]\s*\{([^}]*)\}\)', generator[1])
                        self.assertTrue(dimensions, 'A changed generator requires a new explicit native count audit')
                        self.assertEqual(len(re.findall(r'yield return new TestCaseData\(', generator[1])), 1)
                        self.assertEqual(math.prod(len(dimension.split(',')) for dimension in dimensions), count)
                    else:
                        self.assertEqual(len(re.findall(r'\[(?:Test|UnityTest|TestCase\()', declarations[method])), count)
                    variants = unity_runner.INTEGRATION_VARIANTS.get(suite, {}).get(method)
                    if variants is not None:
                        arguments = re.findall(r'\[TestCase\(([^\r\n]*)\)\]', declarations[method])
                        normalized = {','.join(part.strip().replace('false', 'False').replace('true', 'True')
                                               for part in args.split(',')) for args in arguments}
                        self.assertEqual(normalized, set(variants), 'Pinned integration combinations must remain real test attributes')


class RunnerSourceIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        (self.root / 'package.json').write_text('{"version":"0.11.11-dev.1"}', encoding='utf-8')

    def test_no_git_reports_version_without_machine_paths(self):
        with mock.patch.object(unity_runner.subprocess, 'run', side_effect=FileNotFoundError):
            identity = unity_runner.source_identity(self.root)
        self.assertEqual(identity, {'packageVersion': '0.11.11-dev.1', 'gitCommit': None, 'gitDirty': None})
        self.assertNotIn(str(self.root), json.dumps(identity))

    def test_archive_in_ancestor_repository_does_not_claim_its_commit(self):
        with mock.patch.object(unity_runner.subprocess, 'run', return_value=mock.Mock(returncode=0, stdout=str(self.root.parent))):
            self.assertIsNone(unity_runner.source_identity(self.root)['gitCommit'])

    def test_commit_and_dirty_state_use_only_process_scoped_safe_directory(self):
        commit = 'a' * 40
        for status, dirty in [('', False), (' M Editor/NeutralShapeSampler.cs', True)]:
            with self.subTest(dirty=dirty):
                results = [mock.Mock(returncode=0, stdout=value) for value in [str(self.root), commit, status]]
                with mock.patch.object(unity_runner.subprocess, 'run', side_effect=results) as run:
                    identity = unity_runner.source_identity(self.root)
                self.assertEqual(identity['gitCommit'], commit)
                self.assertEqual(identity['gitDirty'], dirty)
                for call in run.call_args_list:
                    self.assertEqual(call.args[0][:5], ['git', '-c', 'safe.directory=' + str(self.root), '-C', str(self.root)])
                    self.assertNotIn('--global', call.args[0])


class UpstreamTests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads((ROOT / 'Compatibility/dependencies.json').read_text())
        self.releases = {name: {'tag_name': 'v0.0.0', 'draft': False, 'prerelease': False} for name in upstream.REPOSITORIES}

    def test_new_release_requests_review_without_modifying_policy(self):
        before = json.dumps(self.config)
        self.releases['lilToon']['tag_name'] = '2.3.5'
        result = upstream.compare(self.config, self.releases)
        self.assertTrue(result[0]['needsCompatibilityReview'])
        self.assertEqual(before, json.dumps(self.config))

    def test_numeric_version_order_and_existing_release(self):
        self.assertGreater(upstream.release_version('v0.131.10'), upstream.release_version('0.131.2'))
        self.releases['UniVRM']['tag_name'] = max(self.config['uniVrm']['versions'], key=upstream.release_version)
        self.assertFalse(upstream.compare(self.config, self.releases)[1]['needsCompatibilityReview'])

    def test_unrecognized_and_preview_release_never_reports_safe(self):
        for tag in ['0.131.2-preview.1', '0.131', 'latest', ' 0.131.2']:
            with self.assertRaises(ValueError):
                upstream.release_version(tag)
        self.releases['lilToon']['draft'] = True
        with self.assertRaises(ValueError):
            upstream.compare(self.config, self.releases)


if __name__ == '__main__':
    unittest.main()
