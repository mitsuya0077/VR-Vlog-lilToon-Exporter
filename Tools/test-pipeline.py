"""Adversarial regressions for workflow trust and publication receipts."""
import copy
import hashlib
import importlib.util
import io
import json
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]


def tool(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "Tools" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


policy = tool("check-pipeline")
runner = tool("run-validation")
candidate = tool("build-release-candidate")
release = tool("verify-release-candidate")
lint = tool("run-actionlint")
SHA = "a" * 40


def receipt():
    return {"schema": 1, "commit": SHA, "scope": "host-only", "passed": True,
            "python": "3.12.14",
            "source_clean": True,
            "expected_checks": len(runner.plan()),
            "checks": [{"command": cmd[1:], "exit_code": 0, "seconds": 1} for cmd in runner.plan()]}


class WorkflowTrustTests(unittest.TestCase):
    def workflow(self, name="validate.yml"):
        return policy.load_workflow((ROOT / ".github/workflows" / name).read_text())

    def test_current_inventory_passes(self):
        policy.check_all()

    def test_duplicate_keys_and_yaml_boolean_ambiguity(self):
        self.assertIn("on", policy.load_workflow("on:\n  pull_request:\n"))
        for text in ("permissions: {}\npermissions: write-all\n", "jobs:\n  test:\n    steps: []\n    steps: []\n"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                policy.load_workflow(text)

    def test_untrusted_triggers_and_privilege_escalation_fail(self):
        for trigger in ("pull_request_target", "workflow_run", "issue_comment", "schedule"):
            data = self.workflow()
            data["on"][trigger] = None
            with self.subTest(trigger=trigger), self.assertRaises(ValueError):
                policy.check("validate.yml", data)
        data = self.workflow()
        data["jobs"]["schema"]["permissions"] = {"contents": "write"}
        with self.assertRaises(ValueError):
            policy.check("validate.yml", data)

    def test_pr_cannot_receive_secrets_or_persist_checkout_credentials(self):
        mutations = (lambda j: j["steps"][0]["with"].update({"persist-credentials": True}),
                     lambda j: j.update({"env": {"KEY": "${{ secrets.PRIVATE_KEY }}"}}),
                     lambda j: j["steps"][0].update({"uses": "actions/checkout@v4"}),
                     lambda j: j["steps"].append({"run": "echo '${{ github.event.pull_request.title }}'"}))
        for mutation in mutations:
            data = self.workflow()
            mutation(data["jobs"]["schema"])
            with self.assertRaises(ValueError):
                policy.check("validate.yml", data)

    def test_resource_changes_and_ignored_failures_fail(self):
        for change in ({"runs-on": "self-hosted"}, {"runs-on": "ubuntu-latest"}, {"runs-on": "macos-latest"}, {"runs-on": "vrvlog-general-mac-arm64"}, {"runs-on": "ubuntu-8-core"},
                       {"timeout-minutes": 0}, {"timeout-minutes": 360},
                       {"continue-on-error": True}):
            data = self.workflow()
            data["jobs"]["schema"].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                policy.check("validate.yml", data)
        data = self.workflow()
        data["jobs"]["extra"] = copy.deepcopy(data["jobs"]["schema"])
        with self.assertRaises(ValueError):
            policy.check("validate.yml", data)

    def test_missing_or_conditional_full_validation_fails(self):
        for name, job in (("validate.yml", "schema"), ("release-candidate.yml", "candidate"), ("release-vpm.yml", "release")):
            data = self.workflow(name)
            step = next(s for s in data["jobs"][job]["steps"] if s.get("run", "").startswith("python3 Tools/run-validation.py"))
            step["if"] = "false"
            with self.subTest(name=name), self.assertRaises(ValueError):
                policy.check(name, data)

    def test_main_publication_gates_cannot_be_bypassed(self):
        for name, job in (("release-vpm.yml", "release"), ("release-candidate.yml", "candidate"), ("build-listing.yml", "build-listing")):
            data = self.workflow(name)
            data["jobs"][job]["if"] = "true || " + data["jobs"][job]["if"]
            with self.subTest(name=name), self.assertRaises(ValueError):
                policy.check(name, data)

    def test_artifact_retention_and_upload_scope_fail_closed(self):
        for change in ({"retention-days": 90}, {"path": "."}, {"path": "work"}):
            data = self.workflow("release-candidate.yml")
            data["jobs"]["candidate"]["steps"][-1]["with"].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                policy.check("release-candidate.yml", data)

    def test_publication_cannot_skip_the_candidate_receipt_gate(self):
        data = self.workflow("release-vpm.yml")
        step = next(s for s in data["jobs"]["release"]["steps"] if "Tools/verify-release-candidate.py" in s.get("run", ""))
        step["if"] = "false"
        with self.assertRaises(ValueError):
            policy.check("release-vpm.yml", data)

    def test_approved_manual_runs_cannot_replace_pending_runs(self):
        for name in ("release-vpm.yml", "release-candidate.yml"):
            data = self.workflow(name)
            data["concurrency"].pop("queue")
            with self.subTest(name=name), self.assertRaises(ValueError):
                policy.check(name, data)
        data = self.workflow("build-listing.yml")
        data["jobs"]["build-listing"]["concurrency"].pop("queue")
        with self.assertRaises(ValueError):
            policy.check("build-listing.yml", data)


class ValidationReceiptTests(unittest.TestCase):
    def test_commit_changed_during_checks_cannot_receive_a_success_receipt(self):
        state = mock.Mock(side_effect=[(SHA, True), ("b" * 40, True)])
        execute = mock.Mock(return_value=subprocess.CompletedProcess([], 0))
        result = runner.validation_receipt([["python", "one"]], ROOT, execute, state)
        self.assertFalse(result["passed"])
        self.assertEqual(SHA, result["commit"])

    def test_first_failure_or_missing_executable_stops_the_plan(self):
        execute = mock.Mock(side_effect=[subprocess.CompletedProcess([], 1)])
        results = runner.run_checks([["python", "one"], ["python", "two"]], ROOT, execute)
        self.assertEqual(1, len(results))
        self.assertEqual(1, execute.call_count)
        for failure in (OSError(), subprocess.TimeoutExpired("test", 600)):
            execute = mock.Mock(side_effect=failure)
            self.assertEqual(1, runner.run_checks([["python", "one"]], ROOT, execute)[0]["exit_code"])

    def test_all_transfer_modes_remain_in_the_full_plan(self):
        modes = [cmd[-1] for cmd in runner.plan() if "-CloudAvailabilityMode" in cmd]
        self.assertEqual(["Default", "EditorOnly", "DevelopmentOnly", "DevelopmentEditor"], modes)

    def test_incomplete_failed_wrong_sha_and_fabricated_scope_fail(self):
        candidate.validate_report(receipt(), SHA)
        mutations = (lambda r: r.update({"commit": "b" * 40}),
                     lambda r: r.update({"source_clean": False}),
                     lambda r: r.update({"python": "3.13.1"}),
                     lambda r: r.update({"schema": True}),
                     lambda r: r.update({"scope": "unity"}),
                     lambda r: r.update({"passed": False}),
                     lambda r: r["checks"].pop(),
                     lambda r: r["checks"][0].update({"exit_code": 1}),
                     lambda r: r["checks"][0].update({"exit_code": False}),
                     lambda r: r["checks"].reverse())
        for mutate in mutations:
            report = receipt()
            mutate(report)
            with self.assertRaises(ValueError):
                candidate.validate_report(report, SHA)

    def test_zip_documentation_targets_are_checked_in_delivered_files(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "package.zip"
            for target in ("missing.md", "../outside.md", "%2e%2e/outside.md", "/outside.md"):
                with zipfile.ZipFile(path, "w") as archive:
                    archive.writestr("README.md", "[guide](" + target + ")")
                with self.subTest(target=target), self.assertRaises(ValueError):
                    candidate.check_zip_links(path)
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("docs/README.md", "[guide](../guide.md#usage) [external](https://example.org/guide)")
                archive.writestr("guide.md", "# Usage")
            candidate.check_zip_links(path)

    def test_linter_archive_must_match_pinned_hash(self):
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            lint.binary_from_archive(b"untrusted executable", "0" * 64)

    def test_only_policy_checked_queue_diagnostics_are_compatible(self):
        approved = lint.approved_queue_positions()
        self.assertEqual(3, len(approved))
        path, line, column = next(iter(approved))
        allowed = {"message": lint.QUEUE_MESSAGE, "kind": "syntax-check", "filepath": path,
                   "line": line, "column": column}
        self.assertEqual([], lint.blocking_diagnostics([allowed], approved))
        for change in ({"message": "other error"}, {"kind": "expression"}, {"line": line + 1},
                       {"column": column + 1}, {"filepath": "unapproved.yml"}):
            other = dict(allowed, **change)
            self.assertEqual([other], lint.blocking_diagnostics([allowed, other], approved))
        with self.assertRaises(ValueError):
            lint.blocking_diagnostics("not a diagnostic list", approved)

    def test_queue_compatibility_requires_valid_source_policy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Tools").mkdir()
            (root / "Tools/check-pipeline.py").write_text((ROOT / "Tools/check-pipeline.py").read_text())
            (root / ".github/workflows").mkdir(parents=True)
            for source in (ROOT / ".github/workflows").glob("*.yml"):
                (root / ".github/workflows" / source.name).write_text(source.read_text())
            path = root / ".github/workflows/release-vpm.yml"
            path.write_text(path.read_text().replace("queue: max", "queue: invalid"))
            with self.assertRaises(ValueError):
                lint.approved_queue_positions(root)

    def test_cost_model_is_bounded_and_distinguishes_billable_storage(self):
        cost = tool("estimate-pipeline-cost")
        result = cost.estimate(500, 8)
        self.assertEqual(0, result["incremental_runner_cost_usd"])
        self.assertEqual(37.8, result["average_added_artifact_mib"])
        self.assertAlmostEqual(0.009229, result["incremental_storage_cost_if_all_added_bytes_billable_usd"])
        for counts in ((-1, 2), (1, -2), (True, 2)):
            with self.assertRaises(ValueError):
                cost.estimate(*counts)


class PublicationReceiptTests(unittest.TestCase):
    def run_fixture(self):
        return {"repository": {"full_name": release.REPOSITORY}, "workflow_id": 123,
                "path": release.WORKFLOW, "head_sha": SHA, "head_branch": "main",
                "event": "workflow_dispatch", "status": "completed", "conclusion": "success"}

    def archive(self, metadata_changes=None, extra=None, report=None):
        package = "com.vrvlog.liltoon-vrm-exporter-1.2.3.zip"
        payload = b"immutable reviewed package"
        digest = hashlib.sha256(payload).hexdigest()
        meta = {"schema": 1, "repository": release.REPOSITORY, "commit": SHA, "run_id": 42,
                "version": "1.2.3", "package": package, "package_sha256": digest, "validation_scope": "host-only"}
        meta.update(metadata_changes or {})
        out = io.BytesIO()
        with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("candidate.json", json.dumps(meta))
            archive.writestr("validation.json", json.dumps(report if report is not None else receipt()))
            archive.writestr(package, payload)
            if extra:
                archive.writestr(extra, "untrusted extra")
        return out.getvalue(), digest

    def test_success_receipt_is_bound_to_run_commit_version_and_bytes(self):
        release.verify_run(self.run_fixture(), 123, SHA)
        data, digest = self.archive()
        release.verify_archive(data, SHA, "1.2.3", digest, 42)
        with self.assertRaises(ValueError):
            release.verify_archive(data, SHA, "1.2.3", "b" * 64, 42)

    def test_fork_pr_wrong_workflow_failed_and_in_progress_runs_fail(self):
        changes = ({"repository": {"full_name": "attacker/fork"}}, {"head_sha": "b" * 40},
                   {"workflow_id": 456}, {"path": ".github/workflows/fake.yml"},
                   {"event": "pull_request"}, {"head_branch": "feature"},
                   {"status": "in_progress"}, {"conclusion": "failure"}, {"conclusion": "cancelled"})
        for change in changes:
            run = self.run_fixture()
            run.update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                release.verify_run(run, 123, SHA)

    def test_duplicate_expired_and_oversize_artifacts_fail(self):
        row = {"name": "release-candidate-" + SHA, "expired": False, "size_in_bytes": 100}
        self.assertEqual(row, release.select_artifact([row], SHA))
        for rows in ([], [row, row], [dict(row, expired=True)], [dict(row, size_in_bytes=30 * 1024 * 1024)]):
            with self.assertRaises(ValueError):
                release.select_artifact(rows, SHA)

    def test_altered_metadata_inventory_and_incomplete_receipts_fail(self):
        for changes in ({"repository": "attacker/fork"}, {"run_id": 43}, {"commit": "b" * 40},
                        {"version": "1.2.4"}, {"validation_scope": "unity"}):
            data, digest = self.archive(changes)
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                release.verify_archive(data, SHA, "1.2.3", digest, 42)
        for extra in ("../escape", "logs/private.txt", "candidate.json"):
            data, digest = self.archive(extra=extra)
            with self.assertRaises(ValueError):
                release.verify_archive(data, SHA, "1.2.3", digest, 42)
        data, digest = self.archive(report=dict(receipt(), passed=False))
        with self.assertRaises(ValueError):
            release.verify_archive(data, SHA, "1.2.3", digest, 42)

    def test_archive_decompression_budget_is_enforced(self):
        original, digest = self.archive()
        out = io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(original)) as source, zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name in source.namelist():
                archive.writestr(name, b"x" * (129 * 1024) if name == "candidate.json" else source.read(name))
        with self.assertRaises(ValueError):
            release.verify_archive(out.getvalue(), SHA, "1.2.3", digest, 42)


if __name__ == "__main__":
    unittest.main()
