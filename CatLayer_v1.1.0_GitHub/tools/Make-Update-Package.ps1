param(
    [string]$InstallDir = "$env:LOCALAPPDATA\CatLayer\App",
    [string]$OutputDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $InstallDir)) { throw "CatLayer install folder not found: $InstallDir" }
$versionFile = Join-Path $InstallDir 'VERSION.txt'
if (-not (Test-Path $versionFile)) { throw "VERSION.txt not found in $InstallDir" }
$version = (Get-Content $versionFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw 'VERSION.txt is empty.' }
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

$zipName = "CatLayer_v${version}_update.zip"
$zipPath = Join-Path $OutputDir $zipName
$shaPath = Join-Path $OutputDir 'SHA256.txt'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# The update payload is the installed App folder only. User settings/presets/WebData live outside it.
Compress-Archive -Path (Join-Path $InstallDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $shaPath -Value "$hash  $zipName" -Encoding ASCII

Write-Host "Created: $zipPath"
Write-Host "Created: $shaPath"
Write-Host "SHA-256: $hash"
