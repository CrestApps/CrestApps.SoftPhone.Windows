# Enterprise Deployment

Deploy CrestApps Soft Phone at scale and push (optionally locking) its configuration.

## 1. Distribute the app

Pick one:

- **Microsoft Store / Store for Business** — assign the app to users or devices.
- **Intune (line-of-business MSIX)** — upload the signed `.msix`/`.msixbundle` from the
  GitHub Release and assign it. Deploy the signing certificate to the Trusted People store if
  you sideload an unsigned-by-Store package.
- **SCCM / sideload** — `Add-AppxPackage` the signed package, or use your existing MSIX
  deployment pipeline.

The app targets Windows 10 1809+ / Windows 11 and uses the evergreen WebView2 runtime
(preinstalled on Windows 11; install the Evergreen Runtime on older Windows 10 if absent).

## 2. Push configuration

The app resolves each setting with this precedence (**highest wins**):

1. **Machine policy** — `HKLM\SOFTWARE\Policies\CrestApps\SoftPhone`
2. **User policy** — `HKCU\SOFTWARE\Policies\CrestApps\SoftPhone`
3. **Provisioning file** — `%ProgramData%\CrestApps\SoftPhone\config.json`
4. **User setting** (chosen in the app)
5. **Packaged default**

A setting supplied by any managed layer (1–3) is shown **read-only** in the app with a
"Managed by your organization" note — unless `AllowUserOverrideDomain` is set (see below).

### Settings

| Setting | Registry value (REG_SZ / REG_DWORD) | JSON key | Meaning |
|---|---|---|---|
| Tenant domain | `Domain` (REG_SZ) | `Domain` | Bare host, e.g. `phone.example.com`. |
| Allow domain override | `AllowUserOverrideDomain` (REG_DWORD 0/1) | `AllowUserOverrideDomain` | `1` = managed domain is only a default; users may change it. Default `0` = locked. |
| Start at sign-in | `StartWithWindows` (REG_DWORD 0/1) | `StartWithWindows` | Force auto-start on/off. |
| Ringtone | `RingtoneEnabled` (REG_DWORD 0/1) | `RingtoneEnabled` | Force the incoming ringtone on/off. |

### A. Group Policy (ADMX)

1. Copy `packaging/policy/CrestAppsSoftPhone.admx` to the domain **Central Store**
   (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`) or to
   `%SystemRoot%\PolicyDefinitions` on an admin workstation, and
   `packaging/policy/en-US/CrestAppsSoftPhone.adml` into the matching `en-US` folder.
2. Edit a GPO: **Computer (or User) Configuration → Administrative Templates → CrestApps →
   Soft Phone**. Configure **Tenant domain** and, if desired, **Allow users to change the
   tenant domain**, **Start at sign-in**, **Play ringtone**.

### B. Intune

- **Settings Catalog / Administrative Templates:** ingest the ADMX (Devices → Configuration →
  Import ADMX), then configure the same settings, **or**
- **Custom OMA-URI / registry:** push the registry values above under
  `HKLM\SOFTWARE\Policies\CrestApps\SoftPhone`.

### C. Provisioning file (no GPO)

Deploy `%ProgramData%\CrestApps\SoftPhone\config.json`:

```json
{
  "Domain": "phone.example.com",
  "AllowUserOverrideDomain": false,
  "StartWithWindows": true,
  "RingtoneEnabled": true
}
```

## 3. Verify

On a managed device, open **Settings** in the app: managed fields are disabled and annotated,
and the domain-locked value matches your policy. Use **Settings → Diagnostics → Run
connection test** to confirm the background connection authenticates and reaches the hub.

## Example: force and lock the domain via GPO registry

```reg
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\CrestApps\SoftPhone]
"Domain"="phone.example.com"
"AllowUserOverrideDomain"=dword:00000000
"StartWithWindows"=dword:00000001
```
