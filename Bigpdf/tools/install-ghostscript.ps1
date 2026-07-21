# Install Ghostscript and set GHOSTSCRIPT_PATH for Bigpdf.
# Run in an elevated PowerShell session if possible.

$ErrorActionPreference = "Stop"
$toolsDir = Join-Path $PSScriptRoot "ghostscript"
$installer = Join-Path $env:TEMP "gs-installer.exe"

Write-Host "Downloading Ghostscript Windows x64 installer..."
# Pin to a known release; update the URL when you want a newer version.
$downloadUrl = "https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10051/gs10051w64.exe"
Invoke-WebRequest -Uri $downloadUrl -OutFile $installer

Write-Host "Launching installer (interactive — Ghostscript no longer supports silent install)..."
Start-Process -FilePath $installer -Wait

$gsRoot = "C:\Program Files\gs"
if (-Not (Test-Path $gsRoot)) {
    Write-Warning "Expected install root $gsRoot not found. Finish the installer, then re-run this script or set the path at /admin/tools."
    exit 1
}

$versions = Get-ChildItem -Path $gsRoot -Directory | Sort-Object Name -Descending
if ($versions.Length -eq 0) {
    Write-Warning "No Ghostscript versions found under $gsRoot"
    exit 1
}

$binPath = Join-Path $versions[0].FullName "bin"
$gsExe = Join-Path $binPath "gswin64c.exe"
if (-Not (Test-Path $gsExe)) {
    $gsExe = Join-Path $binPath "gs.exe"
}

if (-Not (Test-Path $gsExe)) {
    Write-Warning "Ghostscript executable not found in $binPath"
    exit 1
}

Write-Host "Setting user environment variable GHOSTSCRIPT_PATH to $gsExe"
setx GHOSTSCRIPT_PATH "$gsExe" | Out-Null

$currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($currentPath -notlike "*$binPath*") {
    Write-Host "Adding $binPath to user PATH"
    [Environment]::SetEnvironmentVariable("Path", "$currentPath;$binPath", "User")
}

# Also write toolpaths.json next to the app so Bigpdf picks it up without a full reboot
$appRoot = Split-Path $PSScriptRoot -Parent
$toolpaths = Join-Path $appRoot "toolpaths.json"
$json = @{ GhostscriptPath = $gsExe; LibreOfficePath = $null; TesseractPath = $null } | ConvertTo-Json
if (Test-Path $toolpaths) {
    try {
        $existing = Get-Content $toolpaths -Raw | ConvertFrom-Json
        $existing.GhostscriptPath = $gsExe
        $json = $existing | ConvertTo-Json
    } catch { }
}
Set-Content -Path $toolpaths -Value $json -Encoding UTF8

Write-Host "Ghostscript ready: $gsExe"
Write-Host "Restart the Bigpdf app (and your IDE/terminal) so PATH/env changes are picked up."
