# Download-and-run installer (Inno Setup)

`SoftPhone.iss` builds a single **`SoftPhone-Setup-vX.Y.Z.exe`** that users download and run.
It installs **per-user (no admin prompt)**, asks for the **tenant domain** and **preferences**
during setup, writes them so the app is configured on first launch, adds Start Menu + autostart,
and offers to launch at the end.

## Build

1. Produce the portable build (self-contained):
   ```
   dotnet publish src/SoftPhone.App/SoftPhone.App.csproj -c Release -r win-x64 --self-contained true ^
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
     -o artifacts/portable/SoftPhone
   ```
   (Set `SOFTPHONE_VERSION` first so the build stamp matches, e.g. `set SOFTPHONE_VERSION=0.1.0`.)
2. Compile the installer with Inno Setup 6 (`winget install JRSoftware.InnoSetup`):
   ```
   ISCC.exe /DAppVersion=0.1.0 /DPayloadDir="<repo>\artifacts\portable\SoftPhone" deploy\installer\SoftPhone.iss
   ```
   Output: `artifacts/installer/SoftPhone-Setup-v0.1.0.exe`.

## Interactive use

Run the setup. The wizard asks for the tenant domain (validated) and three preferences
(start at sign-in, ringtone, always on top), then installs and offers to launch.

## Silent / Intune use

```
SoftPhone-Setup-v0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DOMAIN=phone.example.com
```
`/DOMAIN=` pre-fills the tenant domain so the install needs no interaction. Uninstall silently
with the generated `unins000.exe /VERYSILENT`.

## Notes

- Not code-signed yet → first launch shows SmartScreen ("More info → Run anyway"). Sign the
  produced setup.exe (and the app exe) with a code-signing certificate to remove that.
- Requires the Edge WebView2 Runtime (preinstalled on Windows 11; Evergreen installer for
  Windows 10).
