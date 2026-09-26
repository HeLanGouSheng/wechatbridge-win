# Builds and signs the identity package (msix) that registers WeChatBridge.exe as a Windows share target.
# Output: dist\WeChatBridgeWin.msix and dist\WeChatBridgeWin.cer (public certificate for --register).
# The signing key (dist\dev-cert.pfx) is created once on this machine and is git-ignored.
#
# Usage: .\scripts\build-package.ps1 [-Version 0.1.0.0] [-AnyFileType] [-OutDir dist]
#   -AnyFileType  declare SupportsAnyFileType instead of .zip only (fallback if WeChat does not list the app)
#
# ASCII only in this file: Windows PowerShell 5.1 reads a BOM-less script as ANSI.

[CmdletBinding()]
param(
    [string]$Version = "0.1.0.0",
    [switch]$AnyFileType,
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$kits = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64"
$makeappx = Join-Path $kits "makeappx.exe"
$signtool = Join-Path $kits "signtool.exe"
foreach ($tool in @($makeappx, $signtool)) {
    if (-not (Test-Path $tool)) { throw "Missing $tool - install the Windows 10 SDK (10.0.19041)." }
}

$dist = Join-Path $root $OutDir
$pkgRoot = Join-Path $dist "pkgroot"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
if (Test-Path $pkgRoot) { Remove-Item -Recurse -Force $pkgRoot }
New-Item -ItemType Directory -Force -Path (Join-Path $pkgRoot "Assets") | Out-Null

# 1. Manifest: fill in the version and, optionally, the any-file-type fallback.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$manifestPath = Join-Path $root "packaging\AppxManifest.xml"
$manifest = [System.IO.File]::ReadAllText($manifestPath, $utf8)
$manifest = $manifest.Replace("__VERSION__", $Version)
if ($AnyFileType) {
    $manifest = $manifest.Replace("<!--__ANYFILE__-->", "<uap:SupportsAnyFileType />")
}
[System.IO.File]::WriteAllText((Join-Path $pkgRoot "AppxManifest.xml"), $manifest, $utf8)
Copy-Item (Join-Path $root "packaging\Assets\*.png") (Join-Path $pkgRoot "Assets")

# The certificate subject must equal the manifest's Publisher byte for byte.
$xml = [xml]$manifest
$publisher = $xml.Package.Identity.Publisher
$packageName = $xml.Package.Identity.Name

# 2. Certificate: reuse the one made earlier on this machine, else create it.
$pfx = Join-Path $dist "dev-cert.pfx"
$cer = Join-Path $dist "$packageName.cer"
$thumbFile = Join-Path $dist "dev-cert.thumbprint"
$password = if ($env:BRIDGE_PFX_PASSWORD) { $env:BRIDGE_PFX_PASSWORD } else { "wechatbridge-dev" }
$securePassword = ConvertTo-SecureString $password -AsPlainText -Force

$cert = $null
if ((Test-Path $thumbFile) -and (Test-Path $pfx)) {
    $thumb = (Get-Content $thumbFile -Raw).Trim()
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
}
if ($null -eq $cert) {
    Write-Host "Creating self-signed certificate $publisher"
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable -FriendlyName "$packageName dev signing" `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $securePassword | Out-Null
    Set-Content -Path $thumbFile -Value $cert.Thumbprint -Encoding ascii
}
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null

# 3. Pack (/nv: the manifest references an exe that lives outside the package) and sign.
$msix = Join-Path $dist "$packageName.msix"
if (Test-Path $msix) { Remove-Item -Force $msix }
& $makeappx pack /o /d $pkgRoot /nv /p $msix
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }
& $signtool sign /fd SHA256 /f $pfx /p $password $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

Write-Host "Built $msix (version $Version, publisher $publisher)"
Write-Host "Certificate: $cer"
