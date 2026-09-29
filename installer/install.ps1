param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "TTBLabelValidator"))

$ErrorActionPreference = "Stop"
$source = [IO.Path]::GetFullPath($PSScriptRoot)
$target = [IO.Path]::GetFullPath($InstallDirectory)
if ($source.TrimEnd('\') -eq $target.TrimEnd('\')) {
    throw "Run install.ps1 from the extracted package, not from the installation directory."
}
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem -LiteralPath $source -Force | Where-Object Name -ne "install.ps1" | Copy-Item -Destination $target -Recurse -Force
Copy-Item -LiteralPath (Join-Path $source "install.ps1") -Destination (Join-Path $target "install.ps1")

$programs = [Environment]::GetFolderPath("Programs")
$shortcutPath = Join-Path $programs "TTB Label Validator.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $target "Ttb.LabelWeb.exe"
$shortcut.WorkingDirectory = $target
$shortcut.Description = "Launch the local TTB Label Validator prototype"
$shortcut.Save()

Write-Host "Installed TTB Label Validator to $target"
Write-Host "A Start menu shortcut named 'TTB Label Validator' was created."
