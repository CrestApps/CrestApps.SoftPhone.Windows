<#
.SYNOPSIS
  Creates a self-signed CODE-SIGNING certificate (CN=CrestApps) for signing the installer
  and app during the internal pilot, and exports the public .cer (to trust on managed
  machines) and the private .pfx (for the CI signing secret).

.DESCRIPTION
  A self-signed signature makes Windows show "CrestApps" as the publisher. On machines where
  the exported .cer is trusted (Trusted Publishers + Trusted Root — deploy via Intune/GPO,
  see docs/CODE-SIGNING.md), signed builds install with NO SmartScreen/UAC warning.
  Unmanaged/external users still need the Store or a CA/EV certificate.

.PARAMETER Password
  Password protecting the exported .pfx. Also used to build the CI secret.

.PARAMETER OutDir
  Where to write the .pfx/.cer (default: .\artifacts\signing).

.PARAMETER Years
  Certificate validity (default 3).
#>
param(
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$OutDir = "artifacts\signing",
    [int]$Years = 3
)
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$pfxPath = Join-Path $OutDir 'SoftPhone-CodeSigning.pfx'
$cerPath = Join-Path $OutDir 'SoftPhone-CodeSigning.cer'

Write-Host 'Creating self-signed code-signing certificate (CN=CrestApps)…'
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject 'CN=CrestApps, O=CrestApps' `
    -KeyUsage DigitalSignature `
    -FriendlyName 'CrestApps Soft Phone (self-signed)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears($Years) `
    -KeyExportPolicy Exportable `
    -HashAlgorithm SHA256

$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

Write-Host ""
Write-Host "Thumbprint : $($cert.Thumbprint)"
Write-Host "PFX (keep secret) : $pfxPath"
Write-Host "CER (public, deploy to Trusted Publishers) : $cerPath"
Write-Host ""
Write-Host "For CI, add these repo secrets:"
Write-Host "  SIGNING_PFX_PASSWORD = <the password you passed>"
Write-Host "  SIGNING_PFX_BASE64   = (base64 of the .pfx, printed below)"
Write-Host ""
[Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath))
