# Mock TTB application corpora

`generate_corpus.py` is an outcome-neutral generator. All application values, label text, government-warning variants, expected outcomes, findings, and output PDF names come from a JSON data file.

## Data sets

- `pass-corpus-data.json`: 30 intended-pass cases, 10 per product category.
- `fail-corpus-data.json`: 30 non-passing cases spanning all three product categories.

Each generated case directory contains `fixture.json`, `labels/front.png`, `labels/back.png`, a fillable application PDF, a form-field readback JSON, and a verification JSON.

## Generate

From this directory:

```powershell
python generate_corpus.py pass-corpus-data.json
python generate_corpus.py fail-corpus-data.json
```

Use `--out <directory>` to override `outputDirectory` from the selected JSON file. `pass_corpus.py` and `generate_pass_corpus.py` remain as compatibility entry points and accept the same arguments.

`corpus-data.schema.json` describes both input files. The generator also checks outcome-specific invariants: pass cases must contain no findings and must have matching application/label identity and product category; non-pass cases must declare at least one expected finding.

The businesses, addresses, applications, and labels are synthetic and marked accordingly.

## Reject scenarios

The reject corpus contains one instance of each scenario for wine, distilled spirits, and malt beverages:

- Government-warning heading rendered in regular rather than bold type.
- Government-warning wording changed from `health problems` to `health concerns`.
- Required application brand name left empty while it remains on the label.
- Application product-category checkbox selected for a category that does not match the label.

It also contains:

- Three labels with the government warning completely absent, one per product category.
- One spirits label with `Government Warning:` in mixed case.
- One case each with a missing label brand, class/type, net contents, or producer identity.
- One application/label brand mismatch.
- One application/label producer-address mismatch.
- One imported product missing country of origin.
- One imported product missing importer information.
- One malt-beverage application with no label images attached to the PDF.
