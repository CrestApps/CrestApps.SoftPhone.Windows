# Releasing

The **git tag is the single source of version truth.** Nothing about the version is
hand-edited: pushing a `vX.Y.Z` tag stamps `X.Y.Z` into the assemblies (via the
`SOFTPHONE_VERSION` build variable, see `Directory.Build.props`) and `X.Y.Z.0` into the MSIX
manifest (via `scripts/Set-Version.ps1`). The Settings → Diagnostics-adjacent build stamp
in the app shows the running version + build time.

## Workflows

- **`ci.yml`** (every PR/push): restore → build → unit tests (Core + Notifications) →
  MSIX package (unsigned artifact) → FlaUI UI tests.
- **`release.yml`** — builds every distributable with the correct version, and produces clean,
  downloadable releases even before the Partner Center account exists:
  - **on tag `vX.Y.Z`:** stamp version → build + test → **portable zip**, **installer
    `SoftPhone-Setup-vX.Y.Z.exe`** (Inno Setup), and (when the Store is configured) the **MSIX**
    bundle → cut a **GitHub Release** with them attached. When the `STORE_APP_ID` secret is set,
    the same run also **submits the MSIX to the Microsoft Store and auto-commits it**.
  - **manual (Actions → Release → Run workflow, version input):** builds the same
    installer / portable / MSIX artifacts (downloadable from the run) without creating a Release
    and without submitting to the Store.
  The MSIX and Store-submission steps are both best-effort and gated on `STORE_APP_ID` — a
  packaging or Store hiccup never blocks the installer/zip release, and neither runs at all until
  Partner Center is configured.
- **`publish-manual.yml`** (Actions → Run workflow): the Store-only path — build + submit a
  given version to the **Microsoft Store** on demand (run this once the account is approved and
  the Store secrets are set; a checkbox controls whether it auto-commits the submission).

Every distributable is stamped from one version: assemblies (`SOFTPHONE_VERSION`), the MSIX
manifest (`scripts/Set-Version.ps1`), and the installer/zip filenames.

## Cutting a release

```bash
git tag v1.0.0
git push origin v1.0.0
```

To build the installer/packages **without** releasing (e.g. a pilot drop): Actions →
**Release** → **Run workflow** → enter the version → download the artifacts from the run.

## One-time account & credentials setup (manual)

> These are done once by the maintainer. Ask before creating any of these accounts.

1. **Microsoft Partner Center** — enrol in a Windows developer account, reserve the app
   name **Soft Phone**, create the Store listing once, and note the **Store App ID**.
2. **Azure AD app for the Store submission API** — create an app registration, associate it
   in Partner Center (Account settings → User management → Azure AD applications), and grant
   it Manager access. Collect the tenant id, client id, and a client secret.
3. **Repo secrets** (Settings → Secrets and variables → Actions):
   - `PARTNER_TENANT_ID`, `PARTNER_CLIENT_ID`, `PARTNER_CLIENT_SECRET`, `STORE_APP_ID`
   - Optional sideload signing: `SIGNING_PFX_BASE64` (base64 of the .pfx) and
     `SIGNING_PFX_PASSWORD`.
4. **Store payload config** (committed once under `packaging/store/`):
   - `packaging/store/SBConfig.json` — StoreBroker config (`New-StoreBrokerConfigFile`).
   - `packaging/store/PDP/**` — per-listing description/screenshots (`New-StorePdp`).
   See `scripts/Submit-Store.ps1` for how these are consumed.
5. **Identity** — set the MSIX `Identity/@Name`, `Publisher`, and `<PublisherDisplayName>` in
   `packaging/SoftPhone.Package/Package.appxmanifest` to the values Partner Center assigns for
   the reserved app (Product identity page). These are committed directly in the manifest (they
   replace the sideload placeholders `CrestApps.SoftPhone` / `CN=CrestApps`); the version is the
   only part stamped at build time. Store-signed submissions are re-signed by Microsoft; the
   sideload signature (step 3) is only for Intune/SCCM distribution.

## Notes on the local toolchain

Everyday development builds and unit/UI tests run with the .NET SDK and don't need the
Windows SDK. Building the **MSIX** (and therefore package identity → toasts + start-at-login)
requires the Windows SDK + MSBuild packaging targets, which CI (`windows-latest`) has. If you
want to build the package locally, install the "Windows application development" workload and a
Windows 10/11 SDK in Visual Studio.
