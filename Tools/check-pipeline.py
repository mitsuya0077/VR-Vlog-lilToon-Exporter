"""Enforce this repository's workflow trust and resource policy.

This is a regression guard, not a proof that arbitrary workflow code is safe.
actionlint separately checks GitHub Actions syntax and expressions.
"""
import copy
import json
import re
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = {"validate.yml", "build-listing.yml", "release-vpm.yml",
             "release-candidate.yml", "check-upstream.yml"}
JOBS = {
    "validate.yml": {"schema"}, "release-candidate.yml": {"candidate"},
    "release-vpm.yml": {"release", "dispatch-listing"},
    "check-upstream.yml": {"inspect"},
    "build-listing.yml": {"changes", "check-listing-builder", "listing-validation", "build-listing"},
}
PAGES_GATE = "(github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main') || (github.event_name == 'release' && github.event.release.draft == false)"
WRITE_JOBS = {
    ("release-vpm.yml", "release"): {"contents": "write", "actions": "read"},
    ("release-vpm.yml", "dispatch-listing"): {"contents": "read", "actions": "write"},
    ("build-listing.yml", "build-listing"): {"contents": "read", "pages": "write", "id-token": "write"},
}


class WorkflowLoader(yaml.SafeLoader):
    pass


# GitHub uses YAML 1.2 booleans: a workflow's "on" key must stay a string.
WorkflowLoader.yaml_implicit_resolvers = copy.deepcopy(yaml.SafeLoader.yaml_implicit_resolvers)
for key, resolvers in WorkflowLoader.yaml_implicit_resolvers.items():
    WorkflowLoader.yaml_implicit_resolvers[key] = [r for r in resolvers if r[0] != "tag:yaml.org,2002:bool"]
WorkflowLoader.add_implicit_resolver("tag:yaml.org,2002:bool", re.compile(r"^(?:true|false)$", re.I), list("tTfF"))


def mapping(loader, node):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node)
        if key in result:
            raise ValueError("Duplicate YAML key: " + str(key))
        result[key] = loader.construct_object(value_node)
    return result


WorkflowLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)


def load_workflow(text):
    value = yaml.load(text, Loader=WorkflowLoader)
    if not isinstance(value, dict):
        raise ValueError("Workflow must be a mapping")
    return value


def require(condition, message):
    if not condition:
        raise ValueError(message)


def check(name, workflow):
    events = workflow.get("on")
    require(isinstance(events, dict), name + ": explicit event mapping required")
    allowed = {"pull_request", "push", "workflow_dispatch", "release"}
    require(set(events) <= allowed, name + ": unsafe or unreviewed trigger")
    require(workflow.get("permissions") in ({}, {"contents": "read"}), name + ": default permissions must be read-only")
    require(isinstance(workflow.get("concurrency"), dict), name + ": concurrency required")
    jobs = workflow.get("jobs", {})
    require(set(jobs) == JOBS[name], name + ": job inventory requires review")
    require("secrets." not in json.dumps(workflow.get("env", {})), name + ": global secrets are not approved")
    for job_name, job in jobs.items():
        prefix = name + ": " + job_name
        require(job.get("runs-on") == "exporter-general-mac-arm64", prefix + ": only the isolated Exporter Mac runner is approved")
        timeout = job.get("timeout-minutes")
        require(type(timeout) is int and 1 <= timeout <= 30, prefix + ": bounded timeout required")
        require(job.get("continue-on-error", False) is False, prefix + ": job failures must block")
        permissions = job.get("permissions", workflow["permissions"])
        require(isinstance(permissions, dict), prefix + ": explicit permissions mapping required")
        if "write" in permissions.values():
            require(permissions == WRITE_JOBS.get((name, job_name)), prefix + ": unexpected write permission")
            require(job.get("if"), prefix + ": publication trigger gate required")
        else:
            require(set(permissions.values()) <= {"read", "none"}, prefix + ": unknown permission")
            require("secrets." not in json.dumps(job), prefix + ": read-only jobs must not receive secrets")
        require(job.get("container") is None and job.get("services") is None,
                prefix + ": unreviewed containers/services")
        for step in job.get("steps", []):
            require(step.get("continue-on-error", False) is False, prefix + ": step failures must block")
            action = step.get("uses")
            if action:
                require(re.fullmatch(r"[\w.-]+/[\w./-]+@[0-9a-f]{40}", action), prefix + ": action must be SHA pinned")
                if action.startswith("actions/checkout@"):
                    require(step.get("with", {}).get("persist-credentials") is False, prefix + ": checkout credentials must not persist")
                if action.startswith("actions/upload-artifact@") or action.startswith("actions/upload-pages-artifact@"):
                    days = step.get("with", {}).get("retention-days")
                    require(type(days) is int and 1 <= days <= 7, prefix + ": artifact retention must be bounded")
                    path = step.get("with", {}).get("path", "")
                    require(path in {"work/candidate", "${{ env.listPublishDirectory }}"}, prefix + ": artifact upload scope is not approved")
            require("${{" not in step.get("run", ""), prefix + ": expressions must enter scripts through env")

    if name == "validate.yml":
        require(set(events) == {"pull_request", "push"}, "Validate must run on every PR and main push")
        require(events["push"] == {"branches": ["main"]}, "Validate push must target main")
        require("schema" in jobs, "Required schema check must be preserved")
    if name in {"release-vpm.yml", "release-candidate.yml", "check-upstream.yml"}:
        require(set(events) == {"workflow_dispatch"}, name + ": manual trigger required")
    if name in {"release-vpm.yml", "release-candidate.yml"}:
        inputs = events["workflow_dispatch"].get("inputs", {})
        require(inputs.get("expected_commit", {}).get("required") is True, name + ": expected commit required")
        job = jobs["release" if name == "release-vpm.yml" else "candidate"]
        require(job.get("if") == "github.ref == 'refs/heads/main'", name + ": main-only job required")
        group = "release-vpm" if name == "release-vpm.yml" else "release-candidate"
        require(workflow["concurrency"] == {"group": group, "cancel-in-progress": False, "queue": "max"},
                name + ": preserve and serialize queued manual runs")
    if name in {"validate.yml", "release-vpm.yml", "release-candidate.yml"}:
        job_name = {"validate.yml": "schema", "release-vpm.yml": "release", "release-candidate.yml": "candidate"}[name]
        require(any(step.get("run") == "python3 Tools/run-validation.py --report work/validation.json"
                    and not step.get("if") for step in jobs[job_name]["steps"]), name + ": unconditional complete validation required")
    if name == "release-vpm.yml":
        require(inputs.get("candidate_run_id", {}).get("required") is True, "Publication requires a successful candidate run")
        steps = jobs["release"]["steps"]
        runs = [step.get("run", "") for step in steps]
        gates = [step for step in steps if "Tools/verify-release-candidate.py" in step.get("run", "")]
        require(len(gates) == 1 and not gates[0].get("if"), "Unconditional candidate receipt gate required")
        require(steps.index(gates[0]) < next(i for i, step in enumerate(steps) if step.get("name") == "Publish release"),
                "Candidate verification must precede publication")
        require(any("Tools/run-validation.py" in run for run in runs), "Full publication validation missing")
    if name == "build-listing.yml":
        require("listing-validation" in jobs, "Required listing-validation check must be preserved")
        gate = jobs["build-listing"].get("if", "")
        require(gate == PAGES_GATE,
                "Pages manual deployment must target main")
        require(workflow["concurrency"].get("group") == "listing-${{ github.event.pull_request.number || github.run_id }}",
                "Do not replace pending publication workflows")
        require(jobs["build-listing"].get("concurrency") == {"group": "pages", "cancel-in-progress": False, "queue": "max"},
                "Preserve pending Pages deployments")


def check_all(root=ROOT):
    files = {p.name: p for p in (root / ".github/workflows").iterdir()
             if p.suffix in {".yml", ".yaml"}}
    require(set(files) == WORKFLOWS, "Workflow inventory changed; review the trust and cost policy")
    for name, path in sorted(files.items()):
        check(name, load_workflow(path.read_text(encoding="utf-8")))


if __name__ == "__main__":
    check_all()
    print("Workflow trust/resource policy passed; isolated self-hosted Mac, bounded jobs, pinned actions.")
