"""Build a synthetic TTB F 5100.31 fixture from a JSON configuration."""

from __future__ import annotations

import argparse
from io import BytesIO
import json
from pathlib import Path
import sys

import pypdfium2 as pdfium
from pypdf import PdfReader, PdfWriter
from pypdf.generic import ArrayObject, DecodedStreamObject, NameObject, RectangleObject
from reportlab.lib.colors import HexColor
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas


HERE = Path(__file__).resolve().parent
DEFAULT_SOURCE = HERE.parent / "current-form-validation" / "f510031-current-blank.pdf"

TEXT_FIELDS = {
    "permitNumber": "2.  PLANT REGISTRY/BASIC PERMIT/BREWER'S NO. (Required)",
    "brandName": "6. BRAND NAME (Required)",
    "fancifulName": "7. FANCIFUL NAME (If any)",
    "applicantNameAndAddress": "8. NAME AND ADDRESS OF APPLICANT AS SHOWN ON PLANT REGISTRY, BASIC",
    "mailingAddress": "8a. MAILING ADDRESS, IF DIFFERENT",
    "formula": "9.  FORMULA",
    "grapeVarietals": "10. GRAPE VARIETAL(S) Wine only",
    "wineAppellation": "11.  WINE APPELLATION (If on label)",
    "applicationDate": "16.  DATE OF APPLICATION",
    "signerName": "18.  PRINT NAME OF APPLICANT OR AUTHORIZED AGENT",
}

SOURCE_VALUES = {"Domestic": "/Domes", "Imported": "/Import"}
CATEGORY_VALUES = {
    "Wine": "/Wine",
    "Distilled spirits": "/Spirits",
    "Malt beverages": "/Malt",
}
APPLICATION_TYPE_FIELDS = {
    "labelApproval": ("14a. CERTIFICATE OF LABEL APPROVAL", "/yes"),
    "exemption": ("14b. CERTIFICATE OF EXEMPTION FROM LABEL APPROVAL", "/yes"),
    "distinctiveBottle": ("14c. DISTINCTIVE LIQUOR BOTTLE APPROVAL", "/yes"),
    # The source PDF itself spells this appearance state /yse.
    "resubmission": ("14d. RESUBMISSION AFTER REJECTION", "/yse"),
}


def full_name(obj):
    parts = []
    while obj:
        if obj.get("/T") is not None:
            parts.insert(0, str(obj["/T"]))
        obj = obj["/Parent"].get_object() if "/Parent" in obj else None
    return ".".join(parts)


def require_string(value, name, *, allow_empty=False):
    if not isinstance(value, str) or (not allow_empty and not value.strip()):
        qualifier = "possibly empty " if allow_empty else ""
        raise ValueError(f"{name} must be a {qualifier}string")
    return value


def load_config(path: Path):
    try:
        config = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ValueError(f"Invalid JSON in {path}: {exc}") from exc
    if not isinstance(config, dict):
        raise ValueError("The configuration root must be a JSON object")

    form = config.get("form")
    if not isinstance(form, dict):
        raise ValueError("form must be a JSON object")
    for key in (
        "permitNumber", "year", "serialNumber", "sourceOfProduct", "productCategory",
        "brandName", "applicantNameAndAddress", "applicationDate", "signerName",
    ):
        if key not in form:
            raise ValueError(f"form.{key} is required")
    require_string(form["year"], "form.year")
    require_string(form["serialNumber"], "form.serialNumber")
    if len(form["year"]) != 2 or not form["year"].isdigit():
        raise ValueError("form.year must contain exactly two digits")
    if len(form["serialNumber"]) != 4 or not form["serialNumber"].isdigit():
        raise ValueError("form.serialNumber must contain exactly four digits")
    if form["sourceOfProduct"] not in SOURCE_VALUES:
        raise ValueError("form.sourceOfProduct must be Domestic or Imported")
    if form["productCategory"] not in CATEGORY_VALUES:
        raise ValueError("form.productCategory must be Wine, Distilled spirits, or Malt beverages")
    for key in TEXT_FIELDS:
        if key in form:
            require_string(form[key], f"form.{key}", allow_empty=True)

    application_types = form.get("applicationTypes", {"labelApproval": True})
    if not isinstance(application_types, dict):
        raise ValueError("form.applicationTypes must be a JSON object")
    unknown_types = set(application_types) - set(APPLICATION_TYPE_FIELDS)
    if unknown_types:
        raise ValueError("Unknown application type(s): " + ", ".join(sorted(unknown_types)))
    if any(not isinstance(value, bool) for value in application_types.values()):
        raise ValueError("Every form.applicationTypes value must be true or false")

    labels = config.get("labelImages")
    if not isinstance(labels, list) or not 0 <= len(labels) <= 2:
        raise ValueError("labelImages must contain zero, one, or two label image objects")
    resolved_labels = []
    for index, label in enumerate(labels, 1):
        if not isinstance(label, dict):
            raise ValueError(f"labelImages[{index - 1}] must be an object")
        image_path = path.parent / require_string(label.get("file"), f"labelImages[{index - 1}].file")
        image_path = image_path.resolve()
        if not image_path.is_file():
            raise ValueError(f"Label image does not exist: {image_path}")
        caption = label.get("caption", f"LABEL {index}")
        require_string(caption, f"labelImages[{index - 1}].caption")
        resolved_labels.append({"file": image_path, "caption": caption})

    output_name = require_string(config.get("outputPdf"), "outputPdf")
    if Path(output_name).suffix.lower() != ".pdf":
        raise ValueError("outputPdf must end in .pdf")
    config["_configPath"] = path.resolve()
    config["_labels"] = resolved_labels
    config["_applicationTypes"] = application_types
    return config


def form_values(config):
    form = config["form"]
    values = {field_name: str(form.get(key, "")) for key, field_name in TEXT_FIELDS.items()}
    values.update({
        "YEAR 1": form["year"][0],
        "YEAR 2": form["year"][1],
        "Check Box34": SOURCE_VALUES[form["sourceOfProduct"]],
        "Check Box22": CATEGORY_VALUES[form["productCategory"]],
    })
    values.update({f"SERIAL NUMBER {index}": digit for index, digit in enumerate(form["serialNumber"], 1)})
    for key, (field_name, selected_value) in APPLICATION_TYPE_FIELDS.items():
        values[field_name] = selected_value if config["_applicationTypes"].get(key, False) else "/Off"
    return values


def draw_contained_image(pdf_canvas, image_path, x, y, width, height):
    image = ImageReader(str(image_path))
    image_width, image_height = image.getSize()
    scale = min(width / image_width, height / image_height)
    rendered_width, rendered_height = image_width * scale, image_height * scale
    pdf_canvas.drawImage(
        image,
        x + (width - rendered_width) / 2,
        y + (height - rendered_height) / 2,
        width=rendered_width,
        height=rendered_height,
        preserveAspectRatio=True,
        mask="auto",
    )


def make_overlay(config):
    buffer = BytesIO()
    pdf_canvas = canvas.Canvas(buffer, pagesize=(612, 1008))
    fixture_id = str(config.get("fixtureId", "SYNTHETIC-FIXTURE"))
    pdf_canvas.setFillColor(HexColor("#8b4439"))
    pdf_canvas.setFont("Helvetica-Bold", 8)
    pdf_canvas.drawString(22, 997, f"SYNTHETIC TEST APPLICATION - {fixture_id} - NOT FOR SUBMISSION")

    signature = str(config.get("form", {}).get("signatureText", ""))
    if signature:
        pdf_canvas.setFillColor(HexColor("#223f38"))
        pdf_canvas.setFont("Helvetica", 9)
        pdf_canvas.drawString(129, 505, signature)

    positions = [(33, 52), (327, 52)]
    width, height = 234, 259.2
    for index, label in enumerate(config["_labels"]):
        x, y = positions[index]
        pdf_canvas.setFillColor(HexColor("#555555"))
        pdf_canvas.setFont("Helvetica", 7)
        pdf_canvas.drawString(x, 321, label["caption"])
        draw_contained_image(pdf_canvas, label["file"], x, y, width, height)
        # This vector border lets the downstream mock-layout cropper locate each label.
        pdf_canvas.setStrokeColor(HexColor("#23473e"))
        pdf_canvas.setLineWidth(0.8)
        pdf_canvas.rect(x, y, width, height, fill=0, stroke=1)

    pdf_canvas.save()
    buffer.seek(0)
    return PdfReader(buffer)


def prepare_unencrypted_source(source: Path):
    buffer = BytesIO()
    with pdfium.PdfDocument(source) as document:
        document.save(buffer, flags=3)
    buffer.seek(0)
    return PdfReader(buffer)


def build(config_path: Path, output_override: Path | None = None, *, create_preview=True):
    config_path = config_path.resolve()
    config = load_config(config_path)
    source_setting = config.get("sourceForm")
    source = (config_path.parent / source_setting).resolve() if source_setting else DEFAULT_SOURCE.resolve()
    if not source.is_file():
        raise ValueError(f"Source form does not exist: {source}")
    output = output_override.resolve() if output_override else (config_path.parent / config["outputPdf"]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    values = form_values(config)

    reader = prepare_unencrypted_source(source)
    canonical = reader.get_fields() or {}
    missing = sorted(name for name in values if name not in canonical)
    if missing:
        raise ValueError("Source form is missing required fields: " + ", ".join(missing))

    writer = PdfWriter()
    writer.clone_document_from_reader(reader)
    for annotation in writer.pages[0]["/Annots"]:
        obj = annotation.get_object()
        parent = obj.get("/Parent")
        field_type = obj.get("/FT", parent.get_object().get("/FT") if parent else None)
        if field_type == "/Btn" and "/AP" in obj:
            appearances = obj["/AP"]["/N"]
            if not hasattr(appearances, "get_data") and "/Off" not in appearances:
                blank = DecodedStreamObject()
                blank.set_data(b"")
                blank[NameObject("/Type")] = NameObject("/XObject")
                blank[NameObject("/Subtype")] = NameObject("/Form")
                rect = obj["/Rect"]
                blank[NameObject("/BBox")] = RectangleObject([0, 0, abs(rect[2] - rect[0]), abs(rect[3] - rect[1])])
                appearances[NameObject("/Off")] = writer._add_object(blank)

    writer.update_page_form_field_values(None, values, auto_regenerate=False)
    for annotation in writer.pages[0]["/Annots"]:
        obj = annotation.get_object()
        parent = obj.get("/Parent")
        if parent and parent.get_object().get("/T") in ("Check Box34", "Check Box22"):
            obj.pop(NameObject("/V"), None)

    image_field = "AFFIX COMPLETE SET OF LABELS BELOW (See General Instructions 4 and 6)_af_image"
    writer.pages[0][NameObject("/Annots")] = ArrayObject(
        [ref for ref in writer.pages[0]["/Annots"] if full_name(ref.get_object()) != image_field]
    )
    acro_form = writer.root_object["/AcroForm"]
    acro_form[NameObject("/Fields")] = ArrayObject(
        [ref for ref in acro_form["/Fields"] if ref.get_object().get("/T") != image_field]
    )
    writer.pages[0].merge_page(make_overlay(config).pages[0])
    metadata = config.get("metadata", {})
    writer.add_metadata({
        "/Title": str(metadata.get("title", f"{config.get('fixtureId', output.stem)} synthetic TTB application")),
        "/Subject": str(metadata.get("subject", "Invented test data; not a submission or approval")),
    })
    with output.open("wb") as stream:
        writer.write(stream)

    check = PdfReader(output)
    fields = check.get_fields() or {}
    for name, expected in values.items():
        actual = str(fields[name].get("/V", ""))
        if actual != expected:
            raise AssertionError((name, actual, expected))
    extracted = {name: str(fields[name].get("/V", "")) for name in values}
    fields_path = output.with_name(output.stem + "-form-fields.json")
    fields_path.write_text(json.dumps(extracted, indent=2), encoding="utf-8")

    preview_path = output.with_name(output.stem + "-preview.png")
    if create_preview:
        with pdfium.PdfDocument(output) as document:
            document.init_forms()
            document[0].render(scale=1.7, draw_annots=True).to_pil().save(preview_path)
    verification_path = output.with_name(output.stem + "-verification.json")
    verification_path.write_text(json.dumps({
        "configFile": config_path.name,
        "outputPdf": output.name,
        "fieldValuesReadBack": len(values),
        "allFieldsMatched": True,
        "labelImagesEmbedded": [label["file"].name for label in config["_labels"]],
        "previewCreated": create_preview,
        "sourceRevision": "04/2023",
        "syntheticFixture": True,
    }, indent=2), encoding="utf-8")
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path, help="JSON fixture configuration")
    parser.add_argument("--out", type=Path, help="Override outputPdf from the configuration")
    parser.add_argument("--no-preview", action="store_true", help="Skip the full-page PNG preview")
    args = parser.parse_args()
    try:
        output = build(args.config, args.out, create_preview=not args.no_preview)
        print(output)
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
