#!/usr/bin/env python3
"""Create fixture-backed Gemini response envelopes for an application corpus."""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path


PROJECT = Path(__file__).resolve().parent.parent
CORPUS = PROJECT / "test-data" / "mock-corpus"
DEFAULT_INPUT = PROJECT / "work" / "validation-engine-test-input"
DEFAULT_SOURCE_MANIFESTS = [
    CORPUS / "pass-cases" / "manifest.json",
    CORPUS / "fail-cases" / "manifest.json",
]
DEFAULT_DATA_FILES = [
    CORPUS / "pass-corpus-data.json",
    CORPUS / "fail-corpus-data.json",
]
DEFAULT_LIVE_RESULTS: list[Path] = []

FIELD_NAMES = (
    "brandName",
    "classTypeDesignation",
    "wineAppellation",
    "alcoholContent",
    "netContents",
    "bottlerProducerNameAndAddress",
    "countryOfOrigin",
    "governmentWarning",
    "importerNameAndAddress",
)


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT, help="Parsed corpus output directory")
    parser.add_argument("--source-manifest", action="append", type=Path, help="Generated corpus manifest")
    parser.add_argument("--data", action="append", type=Path, help="Corpus source-data JSON")
    parser.add_argument(
        "--live-results",
        action="append",
        type=Path,
        help="Optional run directory containing real Gemini responses; omitted for a fully synthetic corpus",
    )
    return parser.parse_args()


def load_source_cases(manifests: list[Path]) -> dict[str, dict]:
    result = {}
    for manifest_path in manifests:
        document = json.loads(manifest_path.read_text(encoding="utf-8"))
        for case in document["cases"]:
            if case["caseId"] in result:
                raise ValueError(f"Duplicate source case ID: {case['caseId']}")
            result[case["caseId"]] = case
    return result


def load_case_data(paths: list[Path]) -> dict[str, dict]:
    result = {}
    for path in paths:
        document = json.loads(path.read_text(encoding="utf-8"))
        default_warning = document["governmentWarning"]
        for case in document["cases"]:
            value = dict(case)
            value["effectiveGovernmentWarning"] = case["label"].get("governmentWarning", default_warning)
            if case["caseId"] in result:
                raise ValueError(f"Duplicate data case ID: {case['caseId']}")
            result[case["caseId"]] = value
    return result


def load_live_responses(roots: list[Path]) -> dict[str, Path]:
    result = {}
    for root in roots:
        run_manifest_path = root / "run-manifest.json"
        if not run_manifest_path.is_file():
            continue
        run = json.loads(run_manifest_path.read_text(encoding="utf-8"))
        for case in run["cases"]:
            response = case.get("geminiResponse")
            if not response:
                continue
            response_path = root / response
            if not response_path.is_file():
                raise FileNotFoundError(f"Live response is missing: {response_path}")
            if case["caseId"] in result:
                raise ValueError(f"Duplicate live Gemini response for {case['caseId']}")
            result[case["caseId"]] = response_path
    return result


def field(raw, image_id: str) -> dict:
    observed = raw is not None and str(raw).strip() != ""
    return {
        "raw": raw if observed else None,
        "status": "observed" if observed else "not_found",
        "evidence": [{"imageId": image_id, "text": raw}] if observed else [],
        "notes": "",
    }


def fixture_output(case: dict) -> dict:
    label = case["label"]
    warning = case["effectiveGovernmentWarning"] if label.get("present", True) else None
    producer = "\n".join(
        part for part in (label.get("producerQualifier"), label.get("producerNameAndAddress")) if part
    ) or None
    importer = (
        "Imported by\n" + label["importerNameAndAddress"]
        if label.get("importerNameAndAddress")
        else None
    )
    warning_text = f"{warning['heading']} {warning['body']}" if warning else None
    raw_values = {
        "brandName": label.get("brandName") if label.get("present", True) else None,
        "classTypeDesignation": label.get("classTypeDesignation") if label.get("present", True) else None,
        "wineAppellation": label.get("originLine") if label.get("present", True) and label.get("productCategory") == "Wine" else None,
        "alcoholContent": label.get("alcoholContent") if label.get("present", True) else None,
        "netContents": label.get("netContents") if label.get("present", True) else None,
        "bottlerProducerNameAndAddress": producer if label.get("present", True) else None,
        "countryOfOrigin": label.get("countryOfOrigin") if label.get("present", True) else None,
        "governmentWarning": warning_text,
        "importerNameAndAddress": importer if label.get("present", True) else None,
    }
    image_ids = {
        "brandName": "label-01",
        "classTypeDesignation": "label-01",
        "wineAppellation": "label-01",
        "alcoholContent": "label-01",
        "netContents": "label-01",
        "bottlerProducerNameAndAddress": "label-02",
        "countryOfOrigin": "label-02",
        "governmentWarning": "label-02",
        "importerNameAndAddress": "label-02",
    }
    fields = {name: field(raw_values[name], image_ids[name]) for name in FIELD_NAMES}
    warning_formatting = {
        "headingText": warning["heading"] if warning else None,
        "headingAllCaps": warning["headingAllCaps"] if warning else None,
        "headingBold": warning["headingBold"] if warning else None,
        "relativeSize": (
            "smaller"
            if warning and "bodyFontSizePixels" in warning and warning["bodyFontSizePixels"] < 31
            else "similar"
            if warning
            else "undetermined"
        ),
        "evidence": (
            [{"imageId": "label-02", "text": warning["heading"]}] if warning else []
        ),
        "notes": "",
    }
    return {"fields": fields, "warningFormatting": warning_formatting}


def mock_envelope(case_id: str, structured_output: dict) -> dict:
    return {
        "id": "mock_" + case_id.lower(),
        "status": "completed",
        "usage": {
            "total_tokens": 0,
            "total_input_tokens": 0,
            "total_output_tokens": 0,
            "total_thought_tokens": 0,
        },
        "steps": [
            {
                "content": [
                    {
                        "text": json.dumps(structured_output, indent=2, ensure_ascii=False),
                        "type": "text",
                    }
                ],
                "type": "model_output",
            }
        ],
        "object": "interaction",
        "model": "synthetic-fixture",
        "mockMetadata": {
            "synthetic": True,
            "caseId": case_id,
            "generatedFrom": "mock-corpus source data",
        },
    }


def structured_output(envelope: dict) -> dict:
    for step in envelope.get("steps", []):
        if step.get("type") == "model_output":
            for block in step.get("content", []):
                if block.get("type") == "text":
                    return json.loads(block["text"])
    raise ValueError("Gemini response has no structured model output.")


def main() -> int:
    args = arguments()
    input_root = args.input.resolve()
    source_manifests = [p.resolve() for p in (args.source_manifest or DEFAULT_SOURCE_MANIFESTS)]
    data_files = [p.resolve() for p in (args.data or DEFAULT_DATA_FILES)]
    live_roots = [p.resolve() for p in (args.live_results or DEFAULT_LIVE_RESULTS)]
    run_path = input_root / "run-manifest.json"
    if not run_path.is_file():
        raise FileNotFoundError(f"Parsed-corpus run manifest is missing: {run_path}")

    source_cases = load_source_cases(source_manifests)
    case_data = load_case_data(data_files)
    live = load_live_responses(live_roots)
    run = json.loads(run_path.read_text(encoding="utf-8"))
    mocked_count = live_count = 0

    for index, item in enumerate(run["cases"], 1):
        case_id = item["caseId"]
        if case_id not in source_cases or case_id not in case_data:
            raise ValueError(f"No fixture truth was found for {case_id}")
        pdf_path = input_root / item["pdf"]
        prefix = pdf_path.with_suffix("")
        response_path = prefix.with_name(prefix.name + "-gemini-response.json")
        request_manifest_path = prefix.with_name(prefix.name + "-gemini-request-manifest.json")
        parsed_path = input_root / item["json"]

        if case_id in live:
            shutil.copyfile(live[case_id], response_path)
            envelope = json.loads(response_path.read_text(encoding="utf-8"))
            status = "sent"
            synthetic = False
            live_count += 1
        else:
            envelope = mock_envelope(case_id, fixture_output(case_data[case_id]))
            response_path.write_text(json.dumps(envelope, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            status = "mocked"
            synthetic = True
            mocked_count += 1

        extraction = structured_output(envelope)
        # Historical live responses predate the relative-size field. Preserve
        # those raw response files while adapting their parsed observations to
        # the current validation contract.
        extraction["warningFormatting"].setdefault("relativeSize", "undetermined")
        parsed = json.loads(parsed_path.read_text(encoding="utf-8"))
        parsed["labelExtraction"] = {
            "status": status,
            "synthetic": synthetic,
            "responseFile": response_path.name,
            "fields": extraction["fields"],
            "warningFormatting": extraction["warningFormatting"],
        }
        parsed_path.write_text(json.dumps(parsed, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

        request_manifest = json.loads(request_manifest_path.read_text(encoding="utf-8"))
        request_manifest.update(
            status=status,
            responseFile=response_path.name,
            httpStatus=200 if not synthetic else None,
            liveApiValidated=not synthetic,
            synthetic=synthetic,
        )
        request_manifest_path.write_text(
            json.dumps(request_manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        item.update(
            geminiStatus=status,
            geminiResponse=str(response_path.relative_to(input_root)).replace("\\", "/"),
            geminiSynthetic=synthetic,
        )
        print(f"[{index:02}/{len(run['cases']):02}] {case_id}: {status}")

    run["summary"]["geminiRequestsSent"] = live_count
    run["summary"]["geminiResponsesMocked"] = mocked_count
    run["summary"]["geminiResponsesTotal"] = live_count + mocked_count
    run_path.write_text(json.dumps(run, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Completed {live_count} live and {mocked_count} synthetic Gemini responses.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"ERROR: {error}")
        raise SystemExit(1)
