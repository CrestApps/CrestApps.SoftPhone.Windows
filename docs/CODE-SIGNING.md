# Code signing & the "unknown publisher" warning

Unsigned downloads trigger Microsoft Defender SmartScreen ("Windows protected your PC") and
show **Unknown publisher** in UAC. The verified publisher comes **only** from an Authenticode
signature — it can't be set as plain metadata. This doc covers the options.

## Options

| Approach | Cost | Removes SmartScreen? | Best for |
|---|---|---|---|
| **Microsoft Store** | free (dev account) | Yes — Store installs are Microsoft-signed | Everyone, long-term |
| **Self-signed cert + deploy trust** | free | Yes, **on managed machines** where the cert is trusted (Intune/GPO) | Internal fleet (pilot) |
| **Azure Trusted Signing** | ~$10/mo | Yes (validated publisher; reputation builds) | Automated signing of public downloads |
| **EV code-signing cert** | ~$300–500/yr | Yes, **instantly** on day one | Wide external distribution off-Store |

The app's file metadata already shows **CrestApps** (Company) and **Soft Phone** (Product) on
the Details tab regardless of signing — but that does not affect SmartScreen.

## Internal fleet: self-signed + trust (free)

1. **Create the cert** (once):
   ```powershell
   pwsh scripts/New-SigningCert.ps1 -Password "<a-strong-password>"
   ```
   Outputs to `artifacts/signing/`:
   - `SoftPhone-CodeSigning.pfx` — **private key, keep secret**
   - `SoftPhone-CodeSigning.cer` — **public cert, deploy to trust**
   - and prints the base64 of the .pfx for the CI secret.

2. **Add the CI secrets** (repo → Settings → Secrets and variables → Actions):
   - `SIGNING_PFX_BASE64` = the printed base64 (or base64 of the .pfx)
   - `SIGNING_PFX_PASSWORD` = the password you chose

   With these set, `release.yml` signs the **app exe, installer, and MSIX** automatically
   (timestamped) on every build.

3. **Deploy the public cert to your managed machines** so Windows trusts the signature and
   shows **CrestApps** with no warning. Import `SoftPhone-CodeSigning.cer` into **both**:
   - **Trusted Root Certification Authorities** (so the chain validates), and
   - **Trusted Publishers** (so signed installs run without prompting).

   - **Intune:** Devices → Configuration → Create profile → Windows → **Templates → Trusted
     certificate** — one profile targeting the **Computer** *Root* store and another for the
     *Trusted Publishers* store (or use a Settings Catalog / PKCS profile).
   - **Group Policy:** Computer Configuration → Windows Settings → Security Settings → Public
     Key Policies → **Trusted Root Certification Authorities** / **Trusted Publishers** →
     Import the `.cer`.

On machines with the cert trusted, the signed installer runs with **no SmartScreen/UAC
warning** and shows **CrestApps** as the publisher. Unmanaged/external users still need the
Store or a CA/EV certificate, because they don't trust your self-signed root.

## Public downloads: Azure Trusted Signing

For signing public downloads without per-machine trust deployment, use **Azure Trusted
Signing**. Replace the PFX-based signing in `release.yml` with the Trusted Signing action
(`azure/trusted-signing-action`) once you've created a Trusted Signing account and validated
the CrestApps identity. Everything else in the pipeline stays the same.

## Security note

The self-signed `.pfx` is a signing key — treat it like a password. It lives only in
`artifacts/signing/` (git-ignored) and the GitHub secret; never commit it. Regenerate any
time (re-run the script and re-deploy the new `.cer`).
