# Builds the release archive llmsocial's installer downloads: dist\WeChatBridge-win-x64.zip
# (exe + native DLLs + msix + cer + Assets + README) and its SHA-256 sidecar
# dist\WeChatBridge-win-x64.zip.sha256 ("<hex>  <file name>", sha256sum style).
#
# Usage: .\scripts\package-release.ps1 [-SkipPublish]
#   -SkipPublish   reuse the current publish directory instead of running publish.ps1 first.
# ASCII only in this file.

[CmdletBinding()]
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish.ps1")
}

$publish = Join-Path $root "src\Bridge.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
if (-not (Test-Path (Join-Path $publish "WeChatBridge.exe"))) {
    throw "No publish output at $publish. Run scripts\publish.ps1 first."
}

$manifestPath = Join-Path $root "packaging\AppxManifest.xml"
$identity = ([xml][System.IO.File]::ReadAllText($manifestPath, (New-Object System.Text.UTF8Encoding($false)))).Package.Identity
$packageName = $identity.Name

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zipName = "WeChatBridge-win-x64.zip"
$zip = Join-Path $dist $zipName
if (Test-Path $zip) { Remove-Item $zip -Force }

Copy-Item (Join-Path $root "README.md") (Join-Path $publish "README.md") -Force

# Only what the app needs at run time: no .pdb, and no leftovers from earlier identities.
$files = @("WeChatBridge.exe", "$packageName.msix", "$packageName.cer", "Assets", "README.md")
$files += Get-ChildItem -Path $publish -Filter "*.dll" | ForEach-Object { $_.Name }
foreach ($f in $files) {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "Missing $f in $publish" }
}

# Windows 10 1803+ ships bsdtar, which writes zip when the name ends in .zip.
$tar = Join-Path $env:SystemRoot "System32\tar.exe"
Push-Location $publish
try {
    & $tar -a -cf $zip @files
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

$hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$zip.sha256", "$hash  $zipName`n", (New-Object System.Text.ASCIIEncoding))

$size = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Host ""
Write-Host "Release archive: $zip ($size MB)"
Write-Host "SHA-256:         $hash"
Write-Host "Sidecar:         $zip.sha256"
