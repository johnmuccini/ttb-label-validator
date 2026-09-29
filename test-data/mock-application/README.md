# Configurable synthetic application generator

`build_mock.py` fills the current TTB F 5100.31 (04/2023) from a JSON fixture and places zero, one, or two PNG/JPEG label images in the form's label area. Zero images creates an intentionally blank label area for missing-label tests. The JSON contains paths to the images rather than embedding large base64 strings; paths are relative to the JSON file.

## Run

```powershell
python build_mock.py lantern-creek.fixture.json
```

Use `--out another-name.pdf` to override `outputPdf`, or `--no-preview` during bulk generation. Paths in `labelImages[].file` and an optional `sourceForm` are resolved relative to the fixture JSON file.

The example creates:

- `lantern-creek-mock-application.pdf`
- `lantern-creek-mock-application-preview.png`
- `lantern-creek-mock-application-form-fields.json`
- `lantern-creek-mock-application-verification.json`

Copy `lantern-creek.fixture.json` to define another case. Each fixture supplies the form values, output filename, and label image paths. `year` must have two digits and `serialNumber` must have four. Accepted categories are `Wine`, `Distilled spirits`, and `Malt beverages`; accepted sources are `Domestic` and `Imported`.

`fixture.schema.json` documents the complete input structure and enables validation and editor completion in tools that support JSON Schema.

Requires Python with `pypdf`, `pypdfium2`, ReportLab, and Pillow.

The image files remain raster images inside the generated PDF. A vector border is added around each image so the downstream mock-layout cropper can locate it automatically. The generator validates the form values by reading them back and creates fixture-specific preview, field, and verification files.
