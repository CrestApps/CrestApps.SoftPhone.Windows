<#
.SYNOPSIS
  Uploads the built MSIX to the Microsoft Store via StoreBroker (package-only).

.DESCRIPTION
  Called by .github/workflows/release.yml (and publish-manual.yml). Authenticates to the
  Store submission API with an Azure AD app (Partner Center), clones the app's most recent
  submission, replaces its packages with the freshly built bundle, and (unless -NoCommit)
  commits it.

  The listing — description, screenshots, pricing, age rating, privacy policy — is managed in
  Partner Center and is NOT touched here. Cloning carries the existing (valid) listing forward
  unchanged, so CI only ever swaps the binary. This keeps the pipeline simple: no PDP or
  screenshot assets in the repo.

  One-time setup (see docs/RELEASING.md), done by the maintainer:
    • Enroll in Partner Center, reserve the app name, note the Store App ID.
    • Create AND publish the first submission/listing in the portal (description, at least one
      screenshot, privacy policy, age rating). CI can only clone an EXISTING submission — the
      very first one must be created by hand.
    • Create an Azure AD app, associate it in Partner Center (Developer role) → secrets:
        PARTNER_TENANT_ID, PARTNER_CLIENT_ID, PARTNER_CLIENT_SECRET, STORE_APP_ID

.PARAMETER PackagePath
  Folder containing the built .msixupload / .msixbundle (the release artifact).

.PARAMETER NoCommit
  Create/update the submission but leave it uncommitted (a draft) for review in Partner Center.
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
$pkg = Get-ChildItem -Path $PackagePath -Recurse -Include *.msixupload | Select-Object -First 1
if (-not $pkg) {
    $pkg = Get-ChildItem -Path $PackagePath -Recurse -Include *.msixbundle, *.msix |
           Sort-Object Length -Descending | Select-Object -First 1
}
if (-not $pkg) { throw "No .msixupload/.msixbundle/.msix found under $PackagePath." }
Write-Host "Package: $($pkg.FullName)"

# Package-only submission: clone the app's most recent submission, replace its packages with
# the new build, and (unless -NoCommit) commit. -Force clears any half-finished pending
# submission first. The listing is left exactly as it is in Partner Center.
#
# NOTE: this requires at least one existing submission to clone. If the app has never had a
# submission, create (and publish) the first one in the portal — CI cannot bootstrap a listing.
$commit = -not $NoCommit
Update-ApplicationSubmission -AppId $appId `
    -AppxPath $pkg.FullName -ReplacePackages `
    -AutoCommit:$commit -Force

Write-Host "Store package submission complete (AutoCommit=$commit)."
