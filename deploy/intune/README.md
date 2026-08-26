# Internal pilot deployment via Intune

Deploy the Soft Phone **portable build** to managed CrestApps machines silently (no
SmartScreen, no user setup), with the tenant domain pre-filled and locked. No MSIX/Store
required — this wraps the same unpackaged build as the portable zip.

## 1. Build the Win32 package

1. Copy the portable build into this folder so it sits next to `Install.ps1`:
   - `CrestApps.SoftPhone.exe`
   - `Assets\ringtone.wav`
   (From `artifacts/portable/SoftPhone/`, or the `SoftPhone-preview-*.zip` contents.)
2. Download the **Microsoft Win32 Content Prep Tool**
   (https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool) and run:
   ```
   IntuneWinAppUtil.exe -c "<this folder>" -s CrestApps.SoftPhone.exe -o "<output folder>"
   ```
   This produces `CrestApps.SoftPhone.intunewin`.

## 2. Create the Intune app

Intune admin center → **Apps → Windows → Add → Windows app (Win32)** → upload the
`.intunewin`, then set:

- **Install command:** `powershell.exe -ExecutionPolicy Bypass -File Install.ps1`
- **Uninstall command:** `powershell.exe -ExecutionPolicy Bypass -File Uninstall.ps1`
- **Install behavior:** System
- **Detection rule (File):** Path `%ProgramFiles%\CrestApps\Soft Phone`, File
  `CrestApps.SoftPhone.exe`, rule "File or folder exists". (Or use a version-based rule on
  the exe once you stamp production versions.)
- **Assign** to your pilot user/device group.

The installer puts the app in `%ProgramFiles%\CrestApps\Soft Phone`, adds a Start Menu entry,
and adds an all-users Startup shortcut so it launches to the tray at each logon. The app's
single-instance guard makes any duplicate autostart harmless.

## 3. Push the tenant domain (pre-fill + lock)

Deploy `configure-policy.reg`'s values so users never type the domain and can't change it:

- **Edit `configure-policy.reg` first** — set `Domain` to your real tenant (or
  `dialpad-dev.crestapps.online` for the dev pilot).
- **Intune:** Devices → Configuration → **Create profile → Windows → Templates →
  Administrative Templates** (ingest `packaging/policy/CrestAppsSoftPhone.admx`) **or**
  Settings Catalog / a custom profile that writes these values under
  `HKLM\SOFTWARE\Policies\CrestApps\SoftPhone`.
- **Group Policy alternative:** import the ADMX/ADML from `packaging/policy/` and configure
  **Computer → Administrative Templates → CrestApps → Soft Phone**.

With this in place the app skips the setup prompt, shows the domain read-only ("Managed by
your organization"), and connects as soon as the user signs in to the tenant page once.

## 4. WebView2 runtime

Preinstalled on Windows 11. For any Windows 10 machines, deploy the Evergreen WebView2
Runtime (also available as an Intune app from Microsoft) as a dependency.

## Updating the pilot

Rebuild the portable payload, re-wrap, and upload a new version of the Intune app (bump the
detection version). Once the Store listing is live, migrate to the auto-updating MSIX.
