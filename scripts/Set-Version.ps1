<#
.SYNOPSIS
  Stamps a release version (from the git tag) into the MSIX manifest.

.DESCRIPTION
  The tag is the single source of version truth. The .NET assembly version + build stamp
  come from the SOFTPHONE_VERSION environment variable (see Directory.Build.props); this
  script writes the matching 4-part version into packaging/SoftPhone.Package/Package.appxmanifest
  (Identity/@Version = X.Y.Z.0). Never hand-edit the version.

.PARAMETER Version
  Semver like 1.2.3 (no leading v). If omitted, uses $env:SOFTPHONE_VERSION.
#>
param(
    [string]$Version = $env:SOFTPHONE_VERSION
)

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "No version supplied. Pass -Version 1.2.3 or set SOFTPHONE_VERSION."
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must be MAJOR.MINOR.PATCH (e.g. 1.2.3)."
}

$fourPart = "$Version.0"
$manifest = Join-Path $PSScriptRoot "..\packaging\SoftPhone.Package\Package.appxmanifest"
$manifest = (Resolve-Path $manifest).Path

[xml]$xml = Get-Content $manifest
$xml.Package.Identity.Version = $fourPart
$xml.Save($manifest)

Write-Host "Stamped MSIX Identity/@Version = $fourPart into $manifest"
