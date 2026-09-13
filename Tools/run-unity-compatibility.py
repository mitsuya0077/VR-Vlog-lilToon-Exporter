"""Run focused real-Unity tests in an explicit project; never build or publish an app."""
import argparse
import json
import os
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path


def validate_result(xml, supported):
    tree = ET.parse(xml).getroot()
    cases = tree.findall('.//test-case')
    required = ['ActualInstalledPackagesMatchRequestedTestEnvironment']
    if supported:
        required.append('SupportedBackendPreservesMeshesMorphsAndMaterialBindingsOnReimport')
    for name in required:
        found = [c for c in cases if name in c.get('fullname', '')]
        expected_count = 2 if name.startswith('SupportedBackend') else 1
        if len(found) != expected_count or any(c.get('result') != 'Passed' for c in found):
            raise SystemExit('Required real-package test did not pass: ' + name)
    counts = {'DependencyEnvironmentTests': 1, 'DependencyStartupTests': 1}
    if supported:
        counts.update(DependencyRoundTripTests=2, RendererSelectionTests=4, SkinnedMeshFallbackWeightTests=12)
    for suite, count in counts.items():
        found = [c for c in cases if c.get('classname') == 'VRVlog.LilToonExporter.Tests.' + suite]
        if len(found) != count:
            raise SystemExit('Required compatibility suite has missing or unexpected cases: ' + suite)
    if len(cases) != sum(counts.values()):
        raise SystemExit('Unexpected test cases in compatibility run')
    if tree.get('result') != 'Passed' or any(c.get('result') != 'Passed' for c in cases):
        raise SystemExit('Failed or skipped compatibility tests; see ' + str(xml))
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', required=True, type=Path)
    parser.add_argument('--project', required=True, type=Path)
    parser.add_argument('--expect-univrm', required=True)
    parser.add_argument('--expect-unity', default='2022.3.62f3')
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--timeout', type=int, default=900)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    # Unique result names prevent an earlier successful XML from satisfying a failed run.
    stamp = str(time.time_ns())
    xml = output / (stamp + '.xml')
    log = output / (stamp + '.log')
    root = Path(__file__).resolve().parents[1]
    config = json.loads((root / 'Compatibility/dependencies.json').read_text(encoding='utf-8'))
    supported = args.expect_univrm in config['uniVrm']['versions']
    filters = ['VRVlog.LilToonExporter.Tests.DependencyEnvironmentTests',
               'VRVlog.LilToonExporter.Tests.DependencyStartupTests']
    if supported:
        filters.extend(['VRVlog.LilToonExporter.Tests.DependencyRoundTripTests',
                        'VRVlog.LilToonExporter.Tests.RendererSelectionTests',
                        'VRVlog.LilToonExporter.Tests.SkinnedMeshFallbackWeightTests'])
    env = dict(os.environ, VRVLOG_TEST_UNIVRM=args.expect_univrm, VRVLOG_TEST_UNITY=args.expect_unity)
    command = [str(args.unity.resolve()), '-batchmode', '-projectPath', str(args.project.resolve()),
               '-runTests', '-testPlatform', 'EditMode', '-testFilter', ';'.join(filters),
               '-testResults', str(xml), '-logFile', str(log)]
    # GPU-backed editor is needed for real material/texture behavior; no -nographics.
    flags = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name == 'nt' else {}
    started = time.monotonic()
    result = subprocess.run(command, env=env, timeout=args.timeout, **flags)
    if result.returncode != 0 or not xml.exists():
        raise SystemExit('Unity failed or produced no test result. See ' + str(log))
    cases = validate_result(xml, supported)
    report = {'expectedUniVrm': args.expect_univrm, 'unity': args.expect_unity, 'tests': len(cases), 'result': 'Passed',
              'seconds': round(time.monotonic() - started), 'xml': xml.name, 'log': log.name}
    (output / (stamp + '.json')).write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
