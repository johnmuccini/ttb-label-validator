# Installation and local deployment

## Evaluator installation

The release package is a self-contained Windows x64 application. It includes .NET 10, ASP.NET Core, the PDF parser, the validator, rules, and synthetic demonstration data. The evaluator does not need to install .NET, ASP.NET Core, Python, Visual Studio, or a web server.

1. Extract the entire release ZIP to a local directory. Do not open `wwwroot\index.html` directly from the ZIP or extracted folder; that HTML file requires the local controller.
2. Right-click `install.ps1`, choose **Run with PowerShell**, or run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

3. Open **TTB Label Validator** from the Windows Start menu. For a portable run without installation, double-click `START HERE.cmd` in the extracted folder and keep its window open.
4. Open `http://127.0.0.1:5080` in the browser of your choice. The server binds only to that local address. Automatic browser launch is disabled because it proved unreliable with the development machine's configured default browser.
5. To run live extraction, follow **Instructions: Get a free Gemini API key**, enter the test key, and select **Test API key and connection**. The application enables file selection only after that test succeeds.
6. To evaluate without a key or external service, select **Run bundled demonstration using synthetic responses**. These results are clearly marked as synthetic.

The API key remains in server memory only until the application closes. It is not persisted in the browser or on disk.

## Developer installation

Install the .NET 10 SDK on Windows. The SDK contains the .NET runtime, ASP.NET Core runtime, compiler, and templates:

```powershell
winget install Microsoft.DotNet.SDK.10
dotnet --info
```

Python is optional and is required only to regenerate synthetic PDFs or run the Python corpus utilities. Install Python 3.12 or newer and the development packages listed in `requirements-dev.txt`.

Regenerate the source test corpus when changing fixture data:

```powershell
python -m pip install -r .\requirements-dev.txt
python .\test-data\mock-corpus\generate_corpus.py .\test-data\mock-corpus\pass-corpus-data.json
python .\test-data\mock-corpus\generate_corpus.py .\test-data\mock-corpus\fail-corpus-data.json
```

The repository also includes `demo-data`, containing the 60 PDFs and synthetic Gemini responses used by the bundled offline demonstration. It is committed so a release can be built without first regenerating test material.

Build the three C# components:

```powershell
dotnet build .\src\Ttb.LabelParser\Ttb.LabelParser.csproj -c Release
dotnet build .\src\Ttb.LabelValidator\Ttb.LabelValidator.csproj -c Release
dotnet build .\src\Ttb.LabelWeb\Ttb.LabelWeb.csproj -c Release
```

Create the self-contained Windows package:

```powershell
.\tools\publish_local_app.ps1
```

The package is written to `bin\local-app`. Run `Ttb.LabelWeb.exe` or `Start TTB Label Validator.cmd` there. The publish script downloads official .NET runtime packs and NuGet dependencies when they are absent from the local cache.

## Runtime requirements

- Windows x64.
- A browser available on the local machine.
- Port 5080 available on the loopback interface.
- For live extraction, outbound HTTPS access to `generativelanguage.googleapis.com` and a Gemini API key.
- No inbound network access and no administrator privileges are required to run the portable application.

The installer writes the application under `%LOCALAPPDATA%\TTBLabelValidator` and creates a per-user Start menu shortcut. It does not install a Windows service or expose the application on the local network.
