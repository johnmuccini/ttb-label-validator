#!/usr/bin/env python3
"""Integration tests for all five public validator statuses."""

from __future__ import annotations

import json
import shutil
import subprocess
import unittest
import uuid
from pathlib import Path


HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent
VALIDATOR = PROJECT / "bin" / "validator" / "Ttb.LabelValidator.exe"
RULES = PROJECT / "rules" / "validation-rules.json"
INPUT = PROJECT / "work" / "validation-engine-test-input"
RUN = json.loads((INPUT / "run-manifest.json").read_text(encoding="utf-8"))
PASS_CASE = next(case for case in RUN["cases"] if case["caseId"] == "PASS-WINE-01")
FAIL_CASE = next(case for case in RUN["cases"] if case["caseId"] == "FAIL-DSP-11")


def read(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


class ValidatorStatusTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = PROJECT / "work" / "validator-status-tests" / uuid.uuid4().hex
        self.directory.mkdir(parents=True)

    def tearDown(self) -> None:
        shutil.rmtree(self.directory)

    def validate(self, application: Path, response: Path | None = None, manifest: Path | None = None) -> dict:
        output = self.directory / (application.stem + "-result.json")
        command = [
            str(VALIDATOR), "--application", str(application),
            "--rules", str(RULES), "--output", str(output),
        ]
        if response is not None:
            command.extend(["--gemini-response", str(response)])
        if manifest is not None:
            command.extend(["--gemini-manifest", str(manifest)])
        completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(completed.returncode, 0, completed.stderr)
        return read(output)

    def corpus_files(self, case: dict) -> tuple[Path, Path]:
        return INPUT / case["json"], INPUT / case["geminiResponse"]

    def test_approve(self) -> None:
        application, response = self.corpus_files(PASS_CASE)
        self.assertEqual(self.validate(application, response)["status"], "approve")

    def test_reject_and_report_every_failure(self) -> None:
        application, response = self.corpus_files(FAIL_CASE)
        result = self.validate(application, response)
        self.assertEqual(result["status"], "reject")
        self.assertEqual(len(result["findings"]), 6)

    def test_manual_review_for_unreadable_extraction(self) -> None:
        application, response = self.corpus_files(PASS_CASE)
        envelope = read(response)
        model_text = envelope["steps"][0]["content"][0]["text"]
        extraction = json.loads(model_text)
        extraction["fields"]["brandName"].update(raw=None, status="unreadable", evidence=[])
        envelope["steps"][0]["content"][0]["text"] = json.dumps(extraction)
        modified = self.directory / "unreadable-response.json"
        modified.write_text(json.dumps(envelope), encoding="utf-8")
        result = self.validate(application, modified)
        self.assertEqual(result["status"], "manual_review")
        self.assertIn("LABEL_FIELD_UNREADABLE", {finding["code"] for finding in result["findings"]})

    def test_malformed_application_input(self) -> None:
        malformed = self.directory / "malformed-application.json"
        malformed.write_text("{}", encoding="utf-8")
        result = self.validate(malformed)
        self.assertEqual(result["status"], "malformed_input")
        self.assertTrue(result["malformedInputError"])

    def test_external_service_unavailable_after_provider_failure(self) -> None:
        application, _ = self.corpus_files(PASS_CASE)
        copied_application = read(application)
        copied_application.pop("labelExtraction", None)
        local_application = self.directory / "provider-failure.json"
        local_application.write_text(json.dumps(copied_application), encoding="utf-8")
        manifest = self.directory / "provider-failure-manifest.json"
        manifest.write_text(json.dumps({"status": "send_failed", "httpStatus": 503}), encoding="utf-8")
        result = self.validate(local_application, manifest=manifest)
        self.assertEqual(result["status"], "external_service_unavailable")
        self.assertIn("503", result["externalServiceError"])

    def test_malformed_provider_response_is_external_unavailable(self) -> None:
        application, response = self.corpus_files(PASS_CASE)
        envelope = read(response)
        extraction = json.loads(envelope["steps"][0]["content"][0]["text"])
        extraction["fields"]["brandName"]["evidence"] = ["invalid"]
        envelope["steps"][0]["content"][0]["text"] = json.dumps(extraction)
        malformed = self.directory / "malformed-provider-response.json"
        malformed.write_text(json.dumps(envelope), encoding="utf-8")
        result = self.validate(application, malformed)
        self.assertEqual(result["status"], "external_service_unavailable")
        self.assertIn("evidence item", result["externalServiceError"])


if __name__ == "__main__":
    unittest.main()
