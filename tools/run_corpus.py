#!/usr/bin/env python3
"""Run the C# TTB parser over one or more generated-corpus manifests."""

from __future__ import annotations

import argparse
import getpass
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path


PROJECT = Path(__file__).resolve().parent.parent
CORPUS = PROJECT / "test-data" / "mock-corpus"
DEFAULT_PARSER = PROJECT / "bin" / "win-x64" / "Ttb.LabelParser.exe"
DEFAULT_MANIFESTS = [
    CORPUS / "pass-cases" / "manifest.json",
    CORPUS / "fail-cases" / "manifest.json",
]
DEFAULT_OUTPUT = PROJECT / "work" / "validation-engine-test-input"


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Parse every application PDF referenced by the synthetic corpus manifests."
    )
    parser.add_argument("--parser", type=Path, default=DEFAULT_PARSER, help="C# parser .exe or .dll")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="Flat output directory")
    parser.add_argument(
        "--manifest",
        action="append",
        type=Path,
        help="Corpus manifest; repeat for multiple manifests (defaults to pass and fail corpora)",
    )
    parser.add_argument(
        "--send-gemini",
        action="store_true",
        help="Pass --send-gemini to the C# parser (requires GEMINI_API_KEY)",
    )
    parser.add_argument(
        "--case-id",
        action="append",
        help="Run only the named case; repeat to select multiple cases",
    )
    return parser.parse_args()


def load_cases(manifest_path: Path) -> list[dict]:
    with manifest_path.open(encoding="utf-8") as stream:
        document = json.load(stream)
    if not isinstance(document.get("cases"), list):
        raise ValueError(f"Manifest has no cases array: {manifest_path}")
    corpus_root = manifest_path.resolve().parent.parent
    cases = []
    for item in document["cases"]:
        case_id = item["caseId"]
        pdf_path = corpus_root / item["applicationPdf"]
        cases.append(
            {
                "caseId": case_id,
                "pdf": pdf_path,
                "manifest": manifest_path.resolve(),
                "outputGroup": manifest_path.parent.name,
            }
        )
    return cases


def command_for(parser_path: Path, pdf_path: Path, output: Path, send_gemini: bool) -> list[str]:
    prefix = ["dotnet", str(parser_path)] if parser_path.suffix.lower() == ".dll" else [str(parser_path)]
    command = prefix + ["--input", str(pdf_path), "--output", str(output)]
    if send_gemini:
        command.append("--send-gemini")
    return command


def main() -> int:
    args = arguments()
    parser_path = args.parser.resolve()
    manifests = [path.resolve() for path in (args.manifest or DEFAULT_MANIFESTS)]
    output = args.output.resolve()
    if not parser_path.is_file():
        raise FileNotFoundError(f"Parser executable was not found: {parser_path}")
    for manifest in manifests:
        if not manifest.is_file():
            raise FileNotFoundError(f"Manifest was not found: {manifest}")
    output.mkdir(parents=True, exist_ok=True)

    cases = [case for manifest in manifests for case in load_cases(manifest)]
    if args.case_id:
        requested = {case_id.casefold() for case_id in args.case_id}
        cases = [case for case in cases if case["caseId"].casefold() in requested]
        found = {case["caseId"].casefold() for case in cases}
        missing = sorted(requested - found)
        if missing:
            raise ValueError("Unknown case ID(s): " + ", ".join(missing))
    normalized_ids = [case["caseId"].casefold() for case in cases]
    if len(set(normalized_ids)) != len(normalized_ids):
        raise ValueError("Case IDs must be unique across all manifests.")
    grouped_names = [(case["outputGroup"].casefold(), case["pdf"].name.casefold()) for case in cases]
    if len(set(grouped_names)) != len(grouped_names):
        raise ValueError("Original PDF names must be unique within each manifest output group.")

    child_environment = os.environ.copy()
    if args.send_gemini and not child_environment.get("GEMINI_API_KEY"):
        child_environment["GEMINI_API_KEY"] = getpass.getpass("Gemini API key: ").strip()
        if not child_environment["GEMINI_API_KEY"]:
            raise ValueError("A Gemini API key is required when --send-gemini is used.")

    results = []
    for index, case in enumerate(cases, 1):
        pdf_path = case["pdf"]
        if not pdf_path.is_file():
            raise FileNotFoundError(f"Application PDF was not found: {pdf_path}")
        output_name = pdf_path.stem
        case_output = output / case["outputGroup"]
        case_output.mkdir(parents=True, exist_ok=True)
        print(f"[{index:02}/{len(cases):02}] {case['caseId']}", flush=True)
        completed = subprocess.run(
            command_for(parser_path, pdf_path, case_output, args.send_gemini),
            text=True,
            capture_output=True,
            check=False,
            env=child_environment,
        )
        if completed.returncode:
            raise RuntimeError(
                f"Parser failed for {case['caseId']} (exit {completed.returncode}).\n"
                f"stdout:\n{completed.stdout}\nstderr:\n{completed.stderr}"
            )

        json_path = case_output / f"{output_name}.json"
        copied_pdf = case_output / pdf_path.name
        if not json_path.is_file() or not copied_pdf.is_file():
            raise RuntimeError(f"Parser did not create the expected outputs for {case['caseId']}.")
        with json_path.open(encoding="utf-8") as stream:
            parsed = json.load(stream)
        label_files = [case_output / asset["file"] for asset in parsed["labelImages"]]
        if any(not path.is_file() for path in label_files):
            raise RuntimeError(f"A label image is missing for {case['caseId']}.")
        gemini_files = [
            case_output / f"{output_name}-gemini-request.json",
            case_output / f"{output_name}-gemini-response.schema.json",
            case_output / f"{output_name}-gemini-instructions.txt",
            case_output / f"{output_name}-gemini-request-manifest.json",
        ]
        if any(not path.is_file() for path in gemini_files):
            raise RuntimeError(f"A Gemini preparation file is missing for {case['caseId']}.")
        with gemini_files[-1].open(encoding="utf-8") as stream:
            gemini_manifest = json.load(stream)
        gemini_response = case_output / f"{output_name}-gemini-response.json"
        results.append(
            {
                "caseId": case["caseId"],
                "sourcePdf": str(pdf_path),
                "pdf": str(copied_pdf.relative_to(output)).replace("\\", "/"),
                "json": str(json_path.relative_to(output)).replace("\\", "/"),
                "labelImages": [str(path.relative_to(output)).replace("\\", "/") for path in label_files],
                "geminiFiles": [str(path.relative_to(output)).replace("\\", "/") for path in gemini_files],
                "geminiStatus": gemini_manifest["status"],
                "geminiResponse": str(gemini_response.relative_to(output)).replace("\\", "/") if gemini_response.is_file() else None,
            }
        )

    run_manifest = {
        "schemaVersion": "1.0",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "parser": str(parser_path),
        "sourceManifests": [str(path) for path in manifests],
        "summary": {
            "cases": len(results),
            "pdfs": len(results),
            "jsonFiles": len(results),
            "labelImages": sum(len(item["labelImages"]) for item in results),
            "geminiPreparationFiles": sum(len(item["geminiFiles"]) for item in results),
            "geminiRequestsSent": sum(item["geminiStatus"] == "sent" for item in results),
        },
        "cases": results,
    }
    run_manifest_path = output / "run-manifest.json"
    run_manifest_path.write_text(json.dumps(run_manifest, indent=2) + "\n", encoding="utf-8")
    summary = run_manifest["summary"]
    print(
        f"Completed {summary['cases']} cases: {summary['pdfs']} PDFs, "
        f"{summary['jsonFiles']} parsed JSON files, {summary['labelImages']} label images, "
        f"{summary['geminiPreparationFiles']} Gemini preparation files."
    )
    print(run_manifest_path)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
