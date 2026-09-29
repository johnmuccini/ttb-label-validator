param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "TTBLabelValidator"))

$ErrorActionPreference = "Stop"
$target = [IO.Path]::GetFullPath($InstallDirectory)
$localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
if (-not $target.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The uninstall target must be inside LOCALAPPDATA."
}
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "TTB Label Validator.lnk"
if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }
if (Test-Path -LiteralPath $target) {
    if (Get-Process -Name "Ttb.LabelWeb" -ErrorAction SilentlyContinue) {
        throw "Close TTB Label Validator before uninstalling it."
    }
    Set-Location -LiteralPath $env:TEMP
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Host "TTB Label Validator was removed."
} else {
    Write-Host "TTB Label Validator is not installed at $target"
}
