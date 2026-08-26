<#
.SYNOPSIS
  Submits the built MSIX to the Microsoft Store via StoreBroker.

.DESCRIPTION
  Called by .github/workflows/release.yml (and publish-manual.yml). Authenticates to the
  Store submission API with an Azure AD app (Partner Center), then creates a new submission
  that replaces the packages with the freshly built bundle and commits it.

  One-time setup (see docs/RELEASING.md), done by the maintainer:
    • Enroll in Partner Center, reserve the app name, note the Store App ID.
    • Create an Azure AD app and associate it in Partner Center → secrets:
        PARTNER_TENANT_ID, PARTNER_CLIENT_ID, PARTNER_CLIENT_SECRET, STORE_APP_ID
    • Commit the Store payload config under packaging/store/:
        packaging/store/SBConfig.json     (StoreBroker config)
        packaging/store/PDP/**            (per-listing PDP xml + screenshots)
      Generate starters with:  New-StoreBrokerConfigFile ; New-StorePdp

.PARAMETER PackagePath
  Folder containing the built .msixupload / .msixbundle (the release artifact).
#>
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [switch]$NoCommit
)

$ErrorActionPreference = 'Stop'

$tenantId = $env:PARTNER_TENANT_ID
$clientId = $env:PARTNER_CLIENT_ID
$clientSecret = $env:PARTNER_CLIENT_SECRET
$appId = $env:STORE_APP_ID
foreach ($pair in @{TenantId=$tenantId;ClientId=$clientId;ClientSecret=$clientSecret;AppId=$appId}.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($pair.Value)) { throw "Missing $($pair.Key) (set the matching repo secret)." }
}

$storeConfig = Join-Path $PSScriptRoot "..\packaging\store\SBConfig.json"
$pdpRoot = Join-Path $PSScriptRoot "..\packaging\store\PDP"
if (-not (Test-Path $storeConfig)) {
    throw "Store payload config not found at packaging/store/SBConfig.json. Complete the one-time setup in docs/RELEASING.md."
}

Write-Host "Installing StoreBroker…"
if (-not (Get-Module -ListAvailable -Name StoreBroker)) {
    Install-Module -Name StoreBroker -Force -Scope CurrentUser -AllowClobber
}
Import-Module StoreBroker

Write-Host "Authenticating to the Store submission API…"
$secure = ConvertTo-SecureString $clientSecret -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential($clientId, $secure)
Set-StoreBrokerAuthentication -TenantId $tenantId -Credential $cred

# Locate the package to upload (prefer .msixupload, else .msixbundle/.msix).
$pkg = Get-ChildItem -Path $PackagePath -Recurse -Include *.msixupload |
       Select-Object -First 1
if (-not $pkg) {
    $pkg = Get-ChildItem -Path $PackagePath -Recurse -Include *.msixbundle, *.msix |
           Sort-Object Length -Descending | Select-Object -First 1
}
if (-not $pkg) { throw "No .msixupload/.msixbundle/.msix found under $PackagePath." }
Write-Host "Package: $($pkg.FullName)"

$outDir = Join-Path $env:RUNNER_TEMP "sb"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$subJson = Join-Path $outDir "submission.json"
$subZip = Join-Path $outDir "submission.zip"

Write-Host "Building the submission payload…"
New-SubmissionPackage -ConfigPath $storeConfig -PDPRootPath $pdpRoot `
    -AppxPath $pkg.FullName -OutPath $outDir -OutName "submission"

Write-Host "Creating + committing the submission for App $appId…"
$commit = -not $NoCommit
Update-ApplicationSubmission -AppId $appId `
    -SubmissionDataPath $subJson -PackagePath $subZip `
    -ReplacePackages -UpdateListings -AutoCommit:$commit -Force

Write-Host "Store submission complete (AutoCommit=$commit)."
