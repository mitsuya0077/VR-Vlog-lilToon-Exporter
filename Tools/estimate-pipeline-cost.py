"""Estimate this public, self-hosted pipeline's additional Actions cost.

Candidate storage uses the maximum 20 MiB package plus 256 KiB JSON budget,
seven-day retention, and steady monthly arrivals. The upper bound assumes
all added artifact storage is billable; actual shared free allowance is unknown.
"""
import argparse
import json

CANDIDATE_MIB = 20.25
RETENTION_DAYS = 7
ARTIFACT_USD_PER_GIB_MONTH = 0.25


def estimate(pr_runs, candidate_runs, package_mib=CANDIDATE_MIB):
    if any(type(value) is not int or value < 0 for value in (pr_runs, candidate_runs)):
        raise ValueError("Run counts must be nonnegative integers")
    if not 0 < package_mib <= CANDIDATE_MIB:
        raise ValueError("Candidate size exceeds the reviewed budget")
    average_gib = candidate_runs * package_mib / 1024 * RETENTION_DAYS / 30
    return {"repository_visibility": "public", "runner": "isolated self-hosted Mac",
            "monthly_pr_workflow_runs": pr_runs, "monthly_candidate_runs": candidate_runs,
            "candidate_size_mib": package_mib, "candidate_retention_days": RETENTION_DAYS,
            "incremental_runner_cost_usd": 0,
            "average_added_artifact_mib": round(average_gib * 1024, 3),
            "incremental_storage_cost_if_free_allowance_available_usd": 0,
            "incremental_storage_cost_if_all_added_bytes_billable_usd": round(average_gib * ARTIFACT_USD_PER_GIB_MONTH, 6),
            "cache_expansion": False,
            "assumptions": "30-day steady-state month; successful candidates only; shared account free storage not inspected"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pr-runs", type=int, required=True)
    parser.add_argument("--candidate-runs", type=int, required=True)
    parser.add_argument("--candidate-mib", type=float, default=CANDIDATE_MIB)
    args = parser.parse_args()
    print(json.dumps(estimate(args.pr_runs, args.candidate_runs, args.candidate_mib), indent=2))
