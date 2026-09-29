import copy
import json
from pathlib import Path
import unittest

import generate_corpus as generator


HERE = Path(__file__).resolve().parent
TEMP_ROOT = HERE.parent.parent / "work" / "test-corpus-generator"
TEMP_ROOT.mkdir(parents=True, exist_ok=True)


class CorpusGeneratorTests(unittest.TestCase):
    def test_data_files_and_declared_outcomes(self):
        passing = generator.load_data(HERE / "pass-corpus-data.json")
        failing = generator.load_data(HERE / "fail-corpus-data.json")
        self.assertEqual(len(passing["cases"]), 30)
        self.assertTrue(all(case["expectedOutcome"] == "pass" for case in passing["cases"]))
        self.assertEqual(len(failing["cases"]), 30)
        outcomes = [case["expectedOutcome"] for case in failing["cases"]]
        self.assertEqual(outcomes.count("reject"), 30)
        self.assertEqual(outcomes.count("manual_review"), 0)

    def test_fail_scenario_matrix(self):
        failing = generator.load_data(HERE / "fail-corpus-data.json")
        expected_codes = {
            "GOVERNMENT_WARNING_NOT_BOLD",
            "GOVERNMENT_WARNING_TEXT_INCORRECT",
            "APPLICATION_BRAND_NAME_MISSING",
            "PRODUCT_CATEGORY_MISMATCH",
        }
        categories = {"Wine", "Distilled spirits", "Malt beverages"}
        counts = {"Wine": 11, "Distilled spirits": 11, "Malt beverages": 8}
        for category in categories:
            cases = [case for case in failing["cases"] if case["label"]["productCategory"] == category]
            self.assertEqual(len(cases), counts[category])
            codes = {case["expectedFindings"][0]["code"] for case in cases}
            self.assertTrue(expected_codes.issubset(codes))
        missing_warning = [case for case in failing["cases"] if case["expectedFindings"][0]["code"] == "GOVERNMENT_WARNING_MISSING"]
        self.assertEqual({case["label"]["productCategory"] for case in missing_warning}, categories)
        mixed_case = [case for case in failing["cases"] if case["expectedFindings"][0]["code"] == "GOVERNMENT_WARNING_NOT_ALL_CAPS"]
        self.assertEqual(len(mixed_case), 1)
        self.assertEqual(mixed_case[0]["label"]["productCategory"], "Distilled spirits")
        singleton_codes = {
            "LABEL_BRAND_NAME_MISSING", "LABEL_CLASS_TYPE_MISSING", "LABEL_NET_CONTENTS_MISSING",
            "LABEL_PRODUCER_MISSING",
            "COUNTRY_OF_ORIGIN_MISSING", "IMPORTER_INFORMATION_MISSING",
        }
        for code in singleton_codes:
            self.assertEqual(sum(case["expectedFindings"][0]["code"] == code for case in failing["cases"]), 1)
        missing_label = [case for case in failing["cases"] if case["expectedFindings"][0]["code"] == "LABEL_MISSING"]
        self.assertEqual(len(missing_label), 1)
        self.assertEqual(missing_label[0]["label"]["productCategory"], "Malt beverages")
        self.assertFalse(missing_label[0]["label"]["present"])
        self.assertEqual(missing_label[0]["expectedOutcome"], "reject")
        small_type = [case for case in failing["cases"] if case["expectedFindings"][0]["code"] == "GOVERNMENT_WARNING_RELATIVELY_SMALL"]
        self.assertEqual(len(small_type), 2)
        self.assertEqual({case["label"]["governmentWarning"]["bodyFontSizePixels"] for case in small_type}, {12, 23})
        abv_cases = {
            case["expectedFindings"][0]["code"]: case
            for case in failing["cases"]
            if case["expectedFindings"][0]["code"].startswith("ALCOHOL_CONTENT_")
        }
        self.assertEqual(set(abv_cases), {
            "ALCOHOL_CONTENT_BELOW_CLASS_MINIMUM",
            "ALCOHOL_CONTENT_ABOVE_CLASS_MAXIMUM",
        })
        self.assertEqual(abv_cases["ALCOHOL_CONTENT_BELOW_CLASS_MINIMUM"]["label"]["classTypeDesignation"], "WHISKY")
        self.assertEqual(abv_cases["ALCOHOL_CONTENT_BELOW_CLASS_MINIMUM"]["label"]["alcoholContent"], "38% ALC. BY VOL.")
        self.assertEqual(abv_cases["ALCOHOL_CONTENT_ABOVE_CLASS_MAXIMUM"]["label"]["classTypeDesignation"], "DESSERT WINE")
        self.assertEqual(abv_cases["ALCOHOL_CONTENT_ABOVE_CLASS_MAXIMUM"]["label"]["alcoholContent"], "25% ALC. BY VOL.")

        multiple = next(case for case in failing["cases"] if case["caseId"] == "FAIL-DSP-11")
        self.assertEqual(multiple["expectedOutcome"], "reject")
        self.assertEqual(
            {finding["code"] for finding in multiple["expectedFindings"]},
            {
                "ALCOHOL_CONTENT_BELOW_CLASS_MINIMUM",
                "GOVERNMENT_WARNING_NOT_BOLD",
                "GOVERNMENT_WARNING_TEXT_INCORRECT",
                "BRAND_NAME_MISMATCH",
                "PRODUCT_CATEGORY_MISMATCH",
                "PRODUCER_ADDRESS_MISMATCH",
            },
        )
        self.assertEqual(len(multiple["expectedFindings"]), 6)

    def test_pass_mismatch_is_rejected(self):
        data = generator.load_data(HERE / "pass-corpus-data.json")
        changed = copy.deepcopy(data)
        changed["cases"] = [changed["cases"][0]]
        changed["cases"][0]["label"]["brandName"] = "DIFFERENT BRAND"
        path = TEMP_ROOT / "invalid-pass-mismatch.json"
        path.write_text(json.dumps(changed), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "brand names do not match"):
            generator.load_data(path)

    def test_non_pass_case_requires_findings(self):
        data = generator.load_data(HERE / "fail-corpus-data.json")
        changed = copy.deepcopy(data)
        changed["cases"] = [changed["cases"][0]]
        changed["cases"][0]["expectedFindings"] = []
        path = TEMP_ROOT / "invalid-missing-findings.json"
        path.write_text(json.dumps(changed), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "must declare at least one expected finding"):
            generator.load_data(path)


if __name__ == "__main__":
    unittest.main()
