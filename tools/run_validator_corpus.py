#!/usr/bin/env python3
"""Run Ttb.LabelValidator over the prepared pass/fail corpus and verify outcomes."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path


HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent
DEFAULT_INPUT = PROJECT / "work" / "validation-engine-test-input"
DEFAULT_OUTPUT = PROJECT / "work" / "validation-engine-results"
DEFAULT_VALIDATOR = PROJECT / "bin" / "validator" / "Ttb.LabelValidator.exe"
DEFAULT_RULES = PROJECT / "rules" / "validation-rules.json"
SOURCE_MANIFESTS = (
    PROJECT / "test-data" / "mock-corpus" / "pass-cases" / "manifest.json",
    PROJECT / "test-data" / "mock-corpus" / "fail-cases" / "manifest.json",
)


def read_json(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def relative_or_absolute(path: Path, base: Path) -> str:
    try:
        return path.relative_to(base).as_posix()
    except ValueError:
        return str(path)


def expected_cases() -> dict[str, dict]:
    result: dict[str, dict] = {}
    for manifest_path in SOURCE_MANIFESTS:
        for case in read_json(manifest_path)["cases"]:
            result[case["caseId"]] = case
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--validator", type=Path, default=DEFAULT_VALIDATOR)
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--rules", type=Path, default=DEFAULT_RULES)
    parser.add_argument("--case-id", action="append", default=[], help="Run only a selected case; repeat as needed.")
    args = parser.parse_args()

    validator = args.validator.resolve()
    input_root = args.input.resolve()
    output_root = args.output.resolve()
    rules = args.rules.resolve()
    for required in (validator, input_root / "run-manifest.json", rules, *SOURCE_MANIFESTS):
        if not required.exists():
            parser.error(f"required path does not exist: {required}")

    run = read_json(input_root / "run-manifest.json")
    expectations = expected_cases()
    selected = set(args.case_id)
    cases = [case for case in run["cases"] if not selected or case["caseId"] in selected]
    missing = selected - {case["caseId"] for case in cases}
    if missing:
        parser.error("unknown case id(s): " + ", ".join(sorted(missing)))

    if output_root.exists():
        shutil.rmtree(output_root)
    output_root.mkdir(parents=True)

    records: list[dict] = []
    mismatches: list[dict] = []
    status_counts: Counter[str] = Counter()
    for case in cases:
        case_id = case["caseId"]
        expected = expectations[case_id]
        application = input_root / case["json"]
        response = input_root / case["geminiResponse"]
        manifest = input_root / next(
            item for item in case["geminiFiles"] if item.endswith("-gemini-request-manifest.json")
        )
        subdirectory = Path(case["json"]).parent
        result_path = output_root / subdirectory / (application.stem + "-validation-result.json")
        result_path.parent.mkdir(parents=True, exist_ok=True)

        completed = subprocess.run(
            [
                str(validator),
                "--application", str(application),
                "--gemini-response", str(response),
                "--gemini-manifest", str(manifest),
                "--rules", str(rules),
                "--output", str(result_path),
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        if completed.returncode != 0:
            mismatch = {
                "caseId": case_id,
                "kind": "process_error",
                "returnCode": completed.returncode,
                "stderr": completed.stderr.strip(),
            }
            mismatches.append(mismatch)
            records.append(mismatch)
            continue

        result = read_json(result_path)
        actual_status = result["status"]
        expected_status = "approve" if expected["expectedOutcome"] == "pass" else expected["expectedOutcome"]
        expected_codes = sorted(finding["code"] for finding in expected.get("expectedFindings", []))
        actual_codes = sorted(finding["code"] for finding in result["findings"])
        status_counts[actual_status] += 1
        record = {
            "caseId": case_id,
            "expectedStatus": expected_status,
            "actualStatus": actual_status,
            "expectedFindingCodes": expected_codes,
            "actualFindingCodes": actual_codes,
            "matched": expected_status == actual_status and expected_codes == actual_codes,
            "result": relative_or_absolute(result_path, output_root),
        }
        records.append(record)
        if not record["matched"]:
            mismatches.append(record)

    report = {
        "schemaVersion": "1.0",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "validator": str(validator),
        "rules": str(rules),
        "inputManifest": str(input_root / "run-manifest.json"),
        "summary": {
            "cases": len(cases),
            "matched": len(cases) - len(mismatches),
            "mismatched": len(mismatches),
            "statuses": dict(sorted(status_counts.items())),
        },
        "cases": records,
        "mismatches": mismatches,
    }
    report_path = output_root / "validation-run-manifest.json"
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report["summary"], indent=2))
    print(report_path)
    return 1 if mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
