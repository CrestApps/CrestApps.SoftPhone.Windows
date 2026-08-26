# CrestApps Soft Phone — Windows

An always-on Windows tray app that turns your organization's soft phone into a native
desktop companion — so **inbound calls ring even when the phone window is closed**, and a
live WebRTC call keeps running in its own window while you work anywhere else.

It hosts your tenant's first-party `https://{domain}/softphone` page in a dedicated
WebView2 window (the call's media leg), and keeps its **own** authenticated connection to
the tenant's telephony hub alive in the background. That lets it raise a ringing popup +
ringtone and answer, decline, or send to voicemail — even with no window open. It's
**provider-agnostic**: it never loads a telephony SDK and speaks only to your tenant's own
endpoints. The only site it ever contacts is the tenant domain you configure.

## Requirements

This app is a companion to a website running on **OrchardCore** with the **CrestApps
Telephony module** enabled. The server side — the `/softphone` page, the
`/softphone/extension-config` endpoint, and the telephony hub — is provided by:

**➡ [CrestApps.OrchardCore](https://github.com/CrestApps/CrestApps.OrchardCore/) — Telephony module**

The tenant must enable the module's **Soft Phone Extension** feature. You sign in with your
normal site login inside the app — there's no separate password.

## Install

- **Microsoft Store:** install "Soft Phone" and it auto-updates.
- **Sideload (dev/enterprise):** install the signed `.msix` (see
  [Enterprise deployment](docs/ENTERPRISE-DEPLOYMENT.md)).

On first run, enter your tenant domain (e.g. `phone.example.com`). The app validates it,
you sign in, and the phone is ready. It starts with Windows and lives in the system tray;
right-click the tray icon for **Open phone**, **Settings**, and **Quit**. Closing the phone
window keeps the app running in the tray so calls still ring.

## Settings

Open **Settings** from the tray (or the ⚙ in the phone window):

- **General** — tenant domain, start at sign-in, incoming ringtone.
- **Diagnostics** — a **Run connection test** that verifies the background connection can
  authenticate and reach the telephony hub, plus a live connection indicator.

Organizations can push and lock settings (at minimum the domain) via Group Policy or Intune
— see [Enterprise deployment](docs/ENTERPRISE-DEPLOYMENT.md). Managed settings appear
read-only.

## Develop

```bash
dotnet restore CrestApps.SoftPhone.Windows.sln
dotnet build   CrestApps.SoftPhone.Windows.sln -c Debug
dotnet test    tests/SoftPhone.Core.Tests/SoftPhone.Core.Tests.csproj
dotnet test    tests/SoftPhone.Notifications.Tests/SoftPhone.Notifications.Tests.csproj
dotnet test    tests/SoftPhone.UiTests/SoftPhone.UiTests.csproj   # FlaUI, needs a desktop
```

Run the app: `dotnet run --project src/SoftPhone.App`. Useful dev flags:

- `--tray` — start hidden in the tray.
- `--settings` — open Settings directly.
- `--simulate-incoming` — raise a fake incoming call (ringtone + popup) with no live call.

Enable **developer tools** in Settings to add "Simulate incoming call" to the tray menu.

The MSIX package (package identity for toasts + start-at-login) is built through the
packaging project on Windows with the Windows SDK — see
[docs/RELEASING.md](docs/RELEASING.md). Day-to-day development runs unpackaged.

### Layout

- `src/SoftPhone.Core` — UI-free logic: the server contract, config/hub clients, the
  WebView2 cookie bridge, settings, enterprise policy, and the connection diagnostics.
- `src/SoftPhone.Notifications` — UI-free toast payload builder + activation-arg parser.
- `src/SoftPhone.App` — the WPF tray app, WebView2 phone window, and Settings.
- `packaging/` — the MSIX packaging project and the Group Policy (ADMX) templates.
- `tests/` — xUnit unit tests and FlaUI UI tests.

## Tech

.NET 8 · C# · WPF · WebView2 · SignalR .NET client · H.NotifyIcon · MSIX · xUnit · FlaUI ·
GitHub Actions.

## Releasing

Store publishing is automated. Push a `vX.Y.Z` tag to build, package, sign, submit to the
Microsoft Store, and cut a GitHub Release — or use the **Publish (manual)** GitHub Action.
The tag is the single source of version truth. See [docs/RELEASING.md](docs/RELEASING.md).

## Privacy

The app connects only to the tenant domain you configure. It sends no data to the
developer. See [docs/PRIVACY.md](docs/PRIVACY.md).

## License

© CrestApps. All rights reserved.
