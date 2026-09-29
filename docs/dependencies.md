# Dependency inventory

## Deployed application

The release is published for `win-x64` as a self-contained .NET 10 application. All managed and native runtime files are included in the package.

| Component | Dependency | Version | Purpose |
|---|---|---:|---|
| All C# projects | .NET / ASP.NET Core | 10.0 | Runtime, local web server, JSON, HTTP, and process orchestration |
| PDF parser | PdfPig | 0.1.16 | Read AcroForm fields and PDF structure |
| PDF parser | PDFtoImage | 5.4.0 | Render label regions from PDF pages |
| PDF parser, transitive | SkiaSharp and Windows native assets | 4.150.1 | Encode and inspect rendered label images |
| PDF parser, transitive | PDFium Windows native library | 152.0.7961 | PDF rendering engine |
| Web application | Browser-native HTML, CSS, and JavaScript | No package | Local user interface; no Node.js dependency |
| Live extraction | Gemini API, `gemini-3.1-flash-lite` | External service | Structured transcription of label images |

The exact resolved .NET package graph is recorded in `src/Ttb.LabelParser/packages.lock.json`. The validator and web controller use only the shared .NET framework and have no third-party NuGet packages.

## Development and test-data tools

Python is not deployed. The following packages are used only to generate or inspect synthetic test material:

| Package | Version used | Purpose |
|---|---:|---|
| Python | 3.12 or newer | Run generation and corpus orchestration scripts |
| Pillow | 12.3.0 | Render synthetic label artwork |
| pypdf | 6.10.0 | Fill and assemble synthetic PDF applications |
| pypdfium2 | 5.13.0 | Render PDFs during test-data generation |
| ReportLab | 4.4.9 | Create PDF overlays and synthetic content |

All other Python imports in the repository are from the standard library, including `argparse`, `collections`, `copy`, `datetime`, `getpass`, `io`, `json`, `os`, `pathlib`, `re`, `shutil`, `subprocess`, `sys`, `unittest`, and `uuid`.

## Build and packaging tools

- .NET 10 SDK, including the ASP.NET Core SDK.
- PowerShell 5.1 or newer for publishing and installation scripts.
- NuGet access during the first self-contained publish if dependencies are not already cached.
- Git for source control only; it is not a runtime dependency.

## External configuration

- `GEMINI_API_KEY` is supplied to the parser process in memory for live requests. The web interface accepts and validates the key without persisting it.
- Outbound HTTPS to `https://generativelanguage.googleapis.com` is required for key validation and live label extraction.
- The local application binds only to `http://127.0.0.1:5080`.
