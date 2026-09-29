param(
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory,
    [string[]]$RestoreSource = @()
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "bin\local-app"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

$webOutput = $OutputDirectory
$parserOutput = Join-Path $OutputDirectory "components\parser"
$validatorOutput = Join-Path $OutputDirectory "components\validator"

function Publish-Component([string]$Project, [string]$Destination, [string]$Label, [switch]$SingleFile) {
    $restore = @("restore", $Project, "-r", $Runtime, "-p:NuGetAudit=false")
    foreach ($source in $RestoreSource) { $restore += @("--source", $source) }
    & dotnet @restore
    if ($LASTEXITCODE -ne 0) { throw "The $Label restore failed." }
    $publish = @("publish", $Project, "-c", "Release", "-r", $Runtime, "--self-contained", "true", "--no-restore", "-o", $Destination)
    if ($SingleFile) {
        $publish += @(
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true",
            "-p:DebugType=None",
            "-p:DebugSymbols=false"
        )
    }
    & dotnet @publish
    if ($LASTEXITCODE -ne 0) { throw "The $Label publish failed." }
}

Publish-Component (Join-Path $projectRoot "src\Ttb.LabelWeb\Ttb.LabelWeb.csproj") $webOutput "web application" -SingleFile
Publish-Component (Join-Path $projectRoot "src\Ttb.LabelParser\Ttb.LabelParser.csproj") $parserOutput "parser"
Publish-Component (Join-Path $projectRoot "src\Ttb.LabelValidator\Ttb.LabelValidator.csproj") $validatorOutput "validator"

New-Item -ItemType Directory -Path (Join-Path $OutputDirectory "rules") | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "rules\validation-rules.json") -Destination (Join-Path $OutputDirectory "rules\validation-rules.json")

$demoSource = Join-Path $projectRoot "demo-data"
$demoDestination = Join-Path $OutputDirectory "demo-data"
New-Item -ItemType Directory -Path $demoDestination | Out-Null
$manifest = Get-Content (Join-Path $demoSource "run-manifest.json") -Raw | ConvertFrom-Json
Copy-Item -LiteralPath (Join-Path $demoSource "run-manifest.json") -Destination (Join-Path $demoDestination "run-manifest.json")
foreach ($case in $manifest.cases) {
    foreach ($relative in @($case.pdf, $case.geminiResponse)) {
        $source = Join-Path $demoSource $relative
        $destination = Join-Path $demoDestination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
}

Copy-Item -LiteralPath (Join-Path $projectRoot "installer\install.ps1") -Destination (Join-Path $OutputDirectory "install.ps1")
Copy-Item -LiteralPath (Join-Path $projectRoot "installer\uninstall.ps1") -Destination (Join-Path $OutputDirectory "uninstall.ps1")
Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination (Join-Path $OutputDirectory "README.md")
Copy-Item -LiteralPath (Join-Path $projectRoot "ARCHITECTURE.md") -Destination (Join-Path $OutputDirectory "ARCHITECTURE.md")
Copy-Item -LiteralPath (Join-Path $projectRoot "docs") -Destination (Join-Path $OutputDirectory "docs") -Recurse
Set-Content -LiteralPath (Join-Path $OutputDirectory "START HERE.cmd") -Value @(
    '@echo off',
    'title TTB Label Validator',
    'echo Starting the local TTB Label Validator...',
    'echo Keep this window open while using the application.',
    'echo Open http://127.0.0.1:5080 in the browser of your choice.',
    'echo.',
    '"%~dp0Ttb.LabelWeb.exe"',
    'pause'
) -Encoding Ascii
Set-Content -LiteralPath (Join-Path $OutputDirectory "START-HERE.txt") -Value @(
    'TTB Label Validator',
    '',
    '1. Extract the entire ZIP file before running anything.',
    '2. Double-click START HERE.cmd in the extracted folder.',
    '3. Keep the command window open.',
    '4. Open http://127.0.0.1:5080 in a browser.',
    '',
    'Opening wwwroot\index.html directly will not start the local controller.'
) -Encoding UTF8

Write-Host "Self-contained local application published to: $OutputDirectory"
