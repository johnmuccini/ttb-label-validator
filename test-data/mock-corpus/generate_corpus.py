"""Render and build a mock corpus from an external JSON data file."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import sys

from PIL import Image, ImageDraw, ImageFont


HERE = Path(__file__).resolve().parent
MOCK_APP = HERE.parent / "mock-application"
sys.path.insert(0, str(MOCK_APP))
from build_mock import build  # noqa: E402


FONT_DIR = Path("C:/Windows/Fonts")
FONT_REGULAR = FONT_DIR / "arial.ttf"
FONT_BOLD = FONT_DIR / "arialbd.ttf"
FONT_LIGHT = FONT_DIR / "segoeuil.ttf"
FONT_SERIF_BOLD = FONT_DIR / "georgiab.ttf"

# Rendering choices belong to the locked generator. All label and application
# content belongs to the input JSON.
PALETTES = {
    "wine": {"paper": "#f8f1df", "ink": "#24463d", "accent": "#b58a45"},
    "distilled-spirits": {"paper": "#f1ede4", "ink": "#272521", "accent": "#9a5e2f"},
    "malt-beverages": {"paper": "#f4ead3", "ink": "#24372b", "accent": "#c47a28"},
}


def required_string(value, name, *, allow_empty=False):
    if not isinstance(value, str) or (not allow_empty and not value.strip()):
        qualifier = "possibly empty " if allow_empty else ""
        raise ValueError(f"{name} must be a {qualifier}string")
    return value


def slugify(value):
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


def validate_warning(warning, name):
    if not isinstance(warning, dict):
        raise ValueError(f"{name} must be an object")
    heading = required_string(warning.get("heading"), f"{name}.heading")
    required_string(warning.get("body"), f"{name}.body")
    for key in ("headingAllCaps", "headingBold"):
        if not isinstance(warning.get(key), bool):
            raise ValueError(f"{name}.{key} must be true or false")
    if warning["headingAllCaps"] != (heading == heading.upper()):
        raise ValueError(f"{name}.headingAllCaps does not describe the supplied heading")
    for key in ("headingFontSizePixels", "bodyFontSizePixels"):
        if key in warning and (not isinstance(warning[key], int) or isinstance(warning[key], bool) or not 8 <= warning[key] <= 100):
            raise ValueError(f"{name}.{key} must be an integer from 8 through 100")
    return warning


def effective_warning(data, case):
    if "governmentWarning" in case["label"]:
        return case["label"]["governmentWarning"]
    return data["governmentWarning"]


def load_data(path: Path):
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"Invalid JSON in {path}: {exc}") from exc
    if not isinstance(data, dict):
        raise ValueError("The corpus data root must be an object")
    required_string(data.get("outputDirectory"), "outputDirectory")
    warning = validate_warning(data.get("governmentWarning"), "governmentWarning")

    cases = data.get("cases")
    if not isinstance(cases, list) or not cases:
        raise ValueError("cases must be a nonempty array")
    seen = set()
    for index, case in enumerate(cases):
        prefix = f"cases[{index}]"
        if not isinstance(case, dict):
            raise ValueError(f"{prefix} must be an object")
        case_id = required_string(case.get("caseId"), f"{prefix}.caseId")
        if case_id in seen:
            raise ValueError(f"Duplicate caseId: {case_id}")
        seen.add(case_id)
        category = required_string(case.get("categoryFolder"), f"{prefix}.categoryFolder")
        if not re.fullmatch(r"[a-z0-9-]+", category):
            raise ValueError(f"{prefix}.categoryFolder may contain only lowercase letters, numbers, and hyphens")
        output_pdf = required_string(case.get("outputPdf"), f"{prefix}.outputPdf")
        if Path(output_pdf).name != output_pdf or not output_pdf.lower().endswith(".pdf"):
            raise ValueError(f"{prefix}.outputPdf must be a PDF filename without a directory")
        if not isinstance(case.get("metadata", {}), dict):
            raise ValueError(f"{prefix}.metadata must be an object")
        outcome = case.get("expectedOutcome")
        if outcome not in ("pass", "manual_review", "reject"):
            raise ValueError(f"{prefix}.expectedOutcome must be pass, manual_review, or reject")
        findings = case.get("expectedFindings")
        if not isinstance(findings, list) or any(not isinstance(item, dict) for item in findings):
            raise ValueError(f"{prefix}.expectedFindings must be an array of objects")
        for finding_index, finding in enumerate(findings):
            required_string(finding.get("code"), f"{prefix}.expectedFindings[{finding_index}].code")
            required_string(finding.get("field"), f"{prefix}.expectedFindings[{finding_index}].field")
            required_string(finding.get("description"), f"{prefix}.expectedFindings[{finding_index}].description")
        if outcome == "pass" and findings:
            raise ValueError(f"{case_id}: pass cases cannot declare expected findings")
        if outcome != "pass" and not findings:
            raise ValueError(f"{case_id}: non-pass cases must declare at least one expected finding")
        form, label = case.get("form"), case.get("label")
        if not isinstance(form, dict) or not isinstance(label, dict):
            raise ValueError(f"{prefix}.form and {prefix}.label must be objects")
        for key in ("brandName", "fancifulName", "classTypeDesignation", "originLine",
                    "alcoholContent", "netContents", "producerQualifier",
                    "producerNameAndAddress", "productCategory", "palette"):
            required_string(label.get(key), f"{prefix}.label.{key}", allow_empty=key not in ("productCategory", "palette"))
        if label["palette"] not in PALETTES:
            raise ValueError(f"{prefix}.label.palette must be one of: {', '.join(PALETTES)}")
        if not isinstance(label.get("containsSulfites"), bool):
            raise ValueError(f"{prefix}.label.containsSulfites must be true or false")
        if not isinstance(label.get("present", True), bool):
            raise ValueError(f"{prefix}.label.present must be true or false")
        case_warning = effective_warning(data, case)
        if case_warning is not None:
            case_warning = validate_warning(case_warning, f"{prefix}.label.governmentWarning")
        if outcome == "pass":
            comparisons = (
                ("brand names", form.get("brandName"), label["brandName"]),
                ("fanciful names", form.get("fancifulName", ""), label["fancifulName"]),
                ("applicant and producer name/address", form.get("applicantNameAndAddress"), label["producerNameAndAddress"]),
                ("product categories", form.get("productCategory"), label["productCategory"]),
            )
            for description, application_value, label_value in comparisons:
                if application_value != label_value:
                    raise ValueError(f"{case_id}: form and label {description} do not match")
            for key in ("brandName", "classTypeDesignation", "netContents", "producerNameAndAddress"):
                if not label[key].strip():
                    raise ValueError(f"{case_id}: pass case label.{key} cannot be empty")
            if case_warning != warning or not case_warning["headingAllCaps"] or not case_warning["headingBold"]:
                raise ValueError(f"{case_id}: pass case must use the default uppercase, bold government warning")
        for nullable in ("countryOfOrigin", "importerNameAndAddress"):
            if label.get(nullable) is not None and not isinstance(label[nullable], str):
                raise ValueError(f"{prefix}.label.{nullable} must be a string or null")
    return data


def load_font(path, size):
    if not path.is_file():
        raise FileNotFoundError(f"Required font not found: {path}")
    return ImageFont.truetype(str(path), size)


def wrap_pixels(draw, text, text_font, max_width):
    lines, current = [], []
    for word in text.split():
        candidate = " ".join(current + [word])
        if current and draw.textbbox((0, 0), candidate, font=text_font)[2] > max_width:
            lines.append(" ".join(current))
            current = [word]
        else:
            current.append(word)
    if current:
        lines.append(" ".join(current))
    return lines


def centered_lines(draw, lines, y, text_font, fill, spacing=10):
    for line in lines:
        box = draw.textbbox((0, 0), line, font=text_font)
        draw.text(((1000 - (box[2] - box[0])) / 2, y), line, font=text_font, fill=fill)
        y += box[3] - box[1] + spacing
    return y


def draw_motif(draw, palette_name, y, ink, accent):
    if palette_name == "wine":
        for offset in range(7):
            x = 350 + offset * 50
            vertical = (offset % 2) * 24
            draw.ellipse((x, y + vertical, x + 42, y + 42 + vertical), fill=accent)
        draw.line((330, y - 5, 675, y + 70), fill=ink, width=7)
    elif palette_name == "distilled-spirits":
        draw.ellipse((380, y - 20, 620, y + 220), outline=accent, width=8)
        draw.line((500, y - 60, 500, y + 260), fill=ink, width=5)
    else:
        for offset in range(5):
            x = 380 + offset * 60
            draw.ellipse((x, y + offset * 12, x + 90, y + 130 + offset * 12), outline=accent, width=6)
        draw.line((500, y - 30, 500, y + 250), fill=ink, width=6)


def make_label(path: Path, label, warning, side):
    palette_name = label["palette"]
    colors = PALETTES[palette_name]
    image = Image.new("RGB", (1000, 1108), colors["paper"])
    draw = ImageDraw.Draw(image)
    ink, accent = colors["ink"], colors["accent"]
    draw.rounded_rectangle((24, 24, 976, 1084), radius=18, outline=ink, width=8)
    draw.rounded_rectangle((52, 52, 948, 1056), radius=12, outline=accent, width=3)

    brand_font = load_font(FONT_SERIF_BOLD, 64)
    small_font = load_font(FONT_REGULAR, 27)
    type_font = load_font(FONT_BOLD, 43)
    medium_font = load_font(FONT_REGULAR, 31)
    tiny_font = load_font(FONT_REGULAR, 20)
    brand_lines = wrap_pixels(draw, label["brandName"], brand_font, 850) if label["brandName"] else []
    y = centered_lines(draw, brand_lines, 92, brand_font, ink, 8)
    fanciful_lines = [label["fancifulName"]] if label["fancifulName"] else []
    y = centered_lines(draw, fanciful_lines, y + 5, small_font, ink, 6)
    draw.line((180, y + 14, 820, y + 14), fill=accent, width=4)

    if side == "front":
        draw_motif(draw, palette_name, y + 90, ink, accent)
        type_lines = wrap_pixels(draw, label["classTypeDesignation"], type_font, 850) if label["classTypeDesignation"] else []
        centered_lines(draw, type_lines, 565, type_font, ink, 8)
        if label["originLine"]:
            centered_lines(draw, [label["originLine"]], 705, medium_font, ink)
        draw.line((250, 785, 750, 785), fill=accent, width=3)
        contents_line = "     ".join(part for part in (label["alcoholContent"], label["netContents"]) if part)
        centered_lines(draw, [contents_line] if contents_line else [], 820, medium_font, ink)
    else:
        producer_lines = [line for line in [label["producerQualifier"], *label["producerNameAndAddress"].splitlines()] if line]
        y = centered_lines(draw, producer_lines, 260, medium_font, ink, 7)
        if label.get("countryOfOrigin"):
            y = centered_lines(draw, [label["countryOfOrigin"]], y + 8, medium_font, ink, 5)
        if label.get("importerNameAndAddress"):
            importer_lines = ["Imported by", *label["importerNameAndAddress"].splitlines()]
            y = centered_lines(draw, importer_lines, y + 8, load_font(FONT_REGULAR, 27), ink, 4)
        if label["containsSulfites"]:
            y = centered_lines(draw, ["Contains sulfites."], y + 15, medium_font, ink)
        draw.line((110, y + 22, 890, y + 22), fill=accent, width=3)
        warning_y = max(y + 62, 500)
        if warning is not None:
            heading_size = warning.get("headingFontSizePixels", 31)
            body_size = warning.get("bodyFontSizePixels", 25)
            heading_font = load_font(FONT_BOLD if warning["headingBold"] else FONT_LIGHT, heading_size)
            warning_font = load_font(FONT_REGULAR, body_size)
            draw.text((88, warning_y), warning["heading"], font=heading_font, fill="#111111")
            body_y = warning_y + max(28, heading_size + 21)
            for line in wrap_pixels(draw, warning["body"], warning_font, 824):
                draw.text((88, body_y), line, font=warning_font, fill="#111111")
                body_y += max(18, body_size + 9)
    centered_lines(draw, ["FICTIONAL TEST LABEL"], 1004, tiny_font, ink)
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, dpi=(300, 300), optimize=True)


def make_fixture(case, warning, case_dir):
    label = case["label"]
    label_present = label.get("present", True)
    schema_path = MOCK_APP / "fixture.schema.json"
    schema_reference = os.path.relpath(schema_path, case_dir).replace("\\", "/")
    warning_full = f'{warning["heading"]} {warning["body"]}' if warning is not None else None
    return {
        "$schema": schema_reference,
        "fixtureId": case["caseId"],
        "outputPdf": case["outputPdf"],
        "metadata": case.get("metadata", {}),
        "form": case["form"],
        "labelImages": [
            {"caption": "FRONT LABEL", "file": "labels/front.png"},
            {"caption": "BACK LABEL", "file": "labels/back.png"},
        ] if label_present else [],
        "expected": {
            "outcome": case["expectedOutcome"],
            "expectedFindings": case["expectedFindings"],
            "labelFields": {
                "brandName": label["brandName"] if label_present else None,
                "classTypeDesignation": label["classTypeDesignation"] if label_present else None,
                "wineAppellation": label["originLine"] if label_present and label["productCategory"] == "Wine" else None,
                "alcoholContent": label["alcoholContent"] if label_present else None,
                "netContents": label["netContents"] if label_present else None,
                "bottlerProducerNameAndAddress": label["producerNameAndAddress"] if label_present else None,
                "countryOfOrigin": label.get("countryOfOrigin") if label_present else None,
                "governmentWarning": warning_full if label_present else None,
                "importerNameAndAddress": label.get("importerNameAndAddress") if label_present else None,
            },
            "governmentWarningFormatting": {
                "headingText": warning["heading"] if warning is not None else None,
                "headingAllCaps": warning["headingAllCaps"] if warning is not None else None,
                "headingBold": warning["headingBold"] if warning is not None else None,
            },
        },
    }


def generate(data_path: Path, output_override: Path | None = None):
    data_path = data_path.resolve()
    data = load_data(data_path)
    destination = output_override.resolve() if output_override else (data_path.parent / data["outputDirectory"]).resolve()
    manifest = []
    category_counts = {}
    outcome_counts = {}
    for case in data["cases"]:
        warning = effective_warning(data, case)
        directory_brand = case["form"].get("brandName") or case["label"]["brandName"] or case["caseId"]
        directory_name = f'{case["caseId"].lower()}-{slugify(directory_brand)}'
        case_dir = destination / case["categoryFolder"] / directory_name
        case_dir.mkdir(parents=True, exist_ok=True)
        front, back = case_dir / "labels" / "front.png", case_dir / "labels" / "back.png"
        label_present = case["label"].get("present", True)
        if label_present:
            make_label(front, case["label"], warning, "front")
            make_label(back, case["label"], warning, "back")
        fixture = make_fixture(case, warning, case_dir)
        fixture_path = case_dir / "fixture.json"
        fixture_path.write_text(json.dumps(fixture, indent=2, ensure_ascii=False), encoding="utf-8")
        pdf_path = build(fixture_path, create_preview=False)
        label_category = case["label"]["productCategory"]
        application_category = case["form"]["productCategory"]
        category_counts[label_category] = category_counts.get(label_category, 0) + 1
        outcome = case["expectedOutcome"]
        outcome_counts[outcome] = outcome_counts.get(outcome, 0) + 1
        manifest.append({
            "caseId": case["caseId"],
            "labelCategory": label_category,
            "applicationCategory": application_category,
            "expectedOutcome": outcome,
            "expectedFindings": case["expectedFindings"],
            "fixture": str(fixture_path.relative_to(HERE)).replace("\\", "/"),
            "applicationPdf": str(pdf_path.relative_to(HERE)).replace("\\", "/"),
            "frontLabel": str(front.relative_to(HERE)).replace("\\", "/") if label_present else None,
            "backLabel": str(back.relative_to(HERE)).replace("\\", "/") if label_present else None,
        })
        print(f'Generated {case["caseId"]}: {case["label"]["brandName"]}')
    manifest_path = destination / "manifest.json"
    manifest_path.write_text(json.dumps({
        "schemaVersion": "1.0",
        "sourceData": data_path.name,
        "description": data.get("description", "Synthetic TTB application fixtures"),
        "counts": {"total": len(manifest), "byLabelProductCategory": category_counts, "byExpectedOutcome": outcome_counts},
        "cases": manifest,
    }, indent=2), encoding="utf-8")
    return manifest_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("data", type=Path, help="JSON file containing all corpus and case data")
    parser.add_argument("--out", type=Path, help="Override outputDirectory from the data file")
    args = parser.parse_args()
    try:
        manifest = generate(args.data, args.out)
        print(f"Manifest: {manifest}")
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
