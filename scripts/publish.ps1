# Publishes the app (single-file, self-contained, win-x64) and puts the signed identity package and its
# certificate next to the exe. Afterwards:  <publish dir>\WeChatBridge.exe --register
#
# Usage: .\scripts\publish.ps1 [-Version 0.1.0.0] [-AnyFileType] [-Configuration Release]
#
# ASCII only in this file: Windows PowerShell 5.1 reads a BOM-less script as ANSI.

[CmdletBinding()]
param(
    [string]$Version = "",
    [switch]$AnyFileType,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$dotnet = "dotnet"
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $userDotnet)) { $dotnet = $userDotnet }

& $dotnet publish (Join-Path $root "src\Bridge.App\Bridge.App.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$publishDir = Join-Path $root "src\Bridge.App\bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\publish"
if (-not (Test-Path (Join-Path $publishDir "WeChatBridge.exe"))) { throw "Publish output not found at $publishDir" }

$buildArgs = @{}
if ($Version -ne "") { $buildArgs.Version = $Version }
if ($AnyFileType) { $buildArgs.AnyFileType = $true }
& (Join-Path $PSScriptRoot "build-package.ps1") @buildArgs

$dist = Join-Path $root "dist"
# Read as UTF-8 explicitly: the manifest has Chinese in it and Get-Content would decode it as ANSI.
$identity = ([xml][System.IO.File]::ReadAllText((Join-Path $root "packaging\AppxManifest.xml"), (New-Object System.Text.UTF8Encoding($false)))).Package.Identity
$packageName = $identity.Name
$publisherCn = ($identity.Publisher -replace '^CN=', '') -replace '[^A-Za-z0-9._-]', '_'
Copy-Item (Join-Path $dist "$packageName.msix") $publishDir -Force
Copy-Item (Join-Path $dist "$packageName.cer") $publishDir -Force

# Windows resolves a package-with-external-location's logos from the external location (the app folder),
# not from the msix. Without them AppListEntry.DisplayInfo.GetLogo throws 0x80070490 and WeChat drops the
# entry from its list (the system share pane merely shows a blank icon).
New-Item -ItemType Directory -Force -Path (Join-Path $publishDir "Assets") | Out-Null
Copy-Item (Join-Path $root "packaging\Assets\*.png") (Join-Path $publishDir "Assets") -Force

# Sign the exe with the same certificate so its publisher matches the package (no SmartScreen benefit, but consistent).
$kits = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64"
$password = if ($env:BRIDGE_PFX_PASSWORD) { $env:BRIDGE_PFX_PASSWORD } else { "wechatbridge-dev" }
& (Join-Path $kits "signtool.exe") sign /fd SHA256 /f (Join-Path $dist "dev-cert-$publisherCn.pfx") /p $password (Join-Path $publishDir "WeChatBridge.exe") | Out-Null

Write-Host ""
Write-Host "Published to $publishDir"
Write-Host "Next: & '$publishDir\WeChatBridge.exe' --register"
