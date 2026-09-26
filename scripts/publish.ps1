# Publishes the app (single-file, self-contained, win-x64) and puts the signed identity package and its
# certificate next to the exe. Afterwards:  <publish dir>\WeChatBridge.exe --register
#
# Usage: .\scripts\publish.ps1 [-Version 0.1.0.0] [-AnyFileType] [-Configuration Release]
#
# ASCII only in this file: Windows PowerShell 5.1 reads a BOM-less script as ANSI.

[CmdletBinding()]
param(
    [string]$Version = "0.1.0.0",
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

$buildArgs = @{ Version = $Version }
if ($AnyFileType) { $buildArgs.AnyFileType = $true }
& (Join-Path $PSScriptRoot "build-package.ps1") @buildArgs

$dist = Join-Path $root "dist"
Copy-Item (Join-Path $dist "WeChatBridgeWin.msix") $publishDir -Force
Copy-Item (Join-Path $dist "WeChatBridgeWin.cer") $publishDir -Force

# Sign the exe with the same certificate so its publisher matches the package (no SmartScreen benefit, but consistent).
$kits = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64"
$password = if ($env:BRIDGE_PFX_PASSWORD) { $env:BRIDGE_PFX_PASSWORD } else { "wechatbridge-dev" }
& (Join-Path $kits "signtool.exe") sign /fd SHA256 /f (Join-Path $dist "dev-cert.pfx") /p $password (Join-Path $publishDir "WeChatBridge.exe") | Out-Null

Write-Host ""
Write-Host "Published to $publishDir"
Write-Host "Next: & '$publishDir\WeChatBridge.exe' --register"
