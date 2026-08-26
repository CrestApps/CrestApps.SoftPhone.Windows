# CrestApps Soft Phone — Windows Desktop App

**Implementation plan. Self-contained — a session can build the whole app from this document.**

This is the Windows-native sibling of the **CrestApps Soft Phone browser extension**
(`C:\Code\CrestApps\CrestApps.SoftPhone`). It does exactly what the extension does, but
as an always-on Windows tray application distributed through the **Microsoft Store**
(and sideloadable/manageable for enterprises): it hosts the tenant's `/softphone` page
for the live call, keeps its own authenticated connection to the tenant's telephony hub
alive in the background so **inbound calls ring even when no window is open**, shows a
tray icon + Windows toast notifications, and lets the user Answer / Decline / Voicemail.

> **Server side is unchanged.** The `/softphone` page, the `/softphone/extension-config`
> endpoint, and the telephony hub are provided by the **CrestApps.OrchardCore Telephony
> module** (https://github.com/CrestApps/CrestApps.OrchardCore/). The tenant must enable
> its **Soft Phone Extension** feature. This desktop app is just another client of that
> same contract — **no server changes are required.** The "Integration contract" section
> below is the fixed seam (identical to the extension's).

> **Follow the conventions of the browser-extension repo** at
> `C:\Code\CrestApps\CrestApps.SoftPhone` where they fit: version driven by the release
> tag (not hand-edited), a visible build stamp, a **diagnostics toggle** that reveals dev
> tools, a **"simulate incoming call"** command for testing UI without a live call, clean
> user-facing README, and `docs/` for RELEASING / PRIVACY / ABOUT / STORE-LISTING. Commit
> as the repo owner; automate build, test, and store publishing.

---

## 1. Background & decisions (already made)

- **The call is a WebRTC session in the `/softphone` page.** Host that page in a
  **WebView2** control (Chromium/Edge — full WebRTC/`getUserMedia`), exactly as the
  extension hosts it in a popup window. Navigating/closing other things must not destroy
  the call, so the phone lives in its own WebView2 window owned by the tray process.
- **Always-on tray app.** A single-instance process starts at login, shows a tray icon
  (system notification area), and stays running so it can ring at any time.
- **Ring when closed (required).** The tray process keeps its **own** authenticated
  SignalR connection to `TelephonyHub` alive independently of whether the phone window is
  open, so inbound calls raise a **Windows toast** + ringtone and can be answered/declined
  even when the phone window is closed.
- **Provider-agnostic.** Never load a telephony SDK; speak only to the tenant's own
  hub/endpoints (provider-neutral).
- **Auth: the tenant's existing cookie.** The user signs into `{domain}` once inside
  WebView2 (opening the phone triggers the normal OrchardCore login). The background
  connection authenticates using that cookie, read from WebView2's cookie store.
- **Enterprise-manageable.** Organizations can push configuration (e.g., force the tenant
  **domain** for all agents) via **Group Policy (ADMX) / registry / Intune**, and lock it
  so users can't change it. Managed settings are read-only in the UI.

---

## 2. Integration contract (shared with the server — keep identical to the extension)

Everything the app needs from the server. Provided by the OrchardCore Telephony module.

### A. Standalone `/softphone` page
- Gated behind the tenant feature **`CrestApps.OrchardCore.Telephony.SoftPhone.Extension`**.
  A 404 for `/softphone` or `extension-config` means the feature isn't enabled → surface a
  clear "enable the Soft Phone Extension feature on your tenant" message in settings.
- `GET https://{domain}/softphone` — tenant-aware, cookie-authenticated. Unauthenticated →
  302 to the OrchardCore login, returns to `/softphone` after login.
- Renders the soft-phone component full-window (chromeless). Query params:
  - `?host=extension` — embedded by a client: suppress the page's own floating/close chrome.
    (Reuse `host=extension` to avoid a server change; a `host=desktop` alias may be added
    later if desired — do not depend on it.)
  - `?answerCallId={callId}` — on load, auto-answer that pending inbound offer (same path as
    clicking Answer). This is how the app answers from a toast when the window was closed.

### B. Hub + endpoints (exist today; no server change)
- **Hub:** `TelephonyHub` (SignalR). URL is tenant-aware; obtained from the config endpoint
  (C), not hardcoded.
  - Server → client events: `IncomingCall(call, context)`, `CallStateChanged(call)`.
  - Client → server invocations the background may call directly: `Reject(CallReference{ CallId })`,
    `Voicemail(CallReference{ CallId })` — these act on a *ringing* parked call and need no
    media leg.
  - `Answer` is **not** called from the background — answering needs the WebView2 media leg,
    so open/navigate the phone window to `/softphone?answerCallId=…`.
- **Pending offer (catch an in-flight ring on connect):**
  `GET {adminPrefix}/contact-center/agent/current-incoming-offer` → `PendingIncomingCallOffer`
  (`{ Call, Context, ExpiresUtc, ServerTimeUtc }`) or 404 when nothing is ringing.
- **Payload shapes** (fields the client reads):
  - `call`: `{ CallId, From, To, State, Direction, ProviderName }`.
  - `context`: `{ Heading, Cards: [{ Id, Title, Subtitle, Url, OpenInNewTab, Badges }], Properties }`.
    Caller name = `Heading` / `Cards[0].Title`; screen-pop URL = `Cards[].Url` (+ `OpenInNewTab`);
    queue name is typically a `Badge` or `Properties["queueId"]`.

### C. Extension config endpoint
- `GET https://{domain}/softphone/extension-config` — cookie-authenticated JSON:
  ```json
  {
    "hubUrl": "https://{domain}/<tenant-aware telephony hub path>",
    "currentIncomingOfferUrl": "https://{domain}/<adminPrefix>/contact-center/agent/current-incoming-offer",
    "softPhoneUrl": "https://{domain}/softphone",
    "displayName": "Maya Rodriguez",
    "userId": "…"
  }
  ```
  Call it once after the user is authenticated to discover tenant-aware paths, then open the
  background hub connection.

### D. Auth model
- Cookie-based. The app never handles passwords/tokens. It relies on an active `{domain}`
  session cookie held in WebView2's cookie store. If the cookie expires, the background
  connection drops; surface a "sign in again" state and reconnect after the user re-opens
  the phone. **Fallback if the cookie can't be attached to the background connection:** have
  the page (when open) mint a short-lived token via an authenticated endpoint and hand it to
  the background, which uses SignalR `AccessTokenProvider`. Prefer the cookie path.

---

## 3. Architecture

```
┌ CrestApps.SoftPhone.Windows (single-instance tray process) ─────────────────┐
│                                                                             │
│  App host (WPF, .NET 8)                                                      │
│    • tray icon + context menu (Open, Collapse/Expand, Settings, Quit)       │
│    • single-instance guard; autostart via MSIX startupTask                  │
│    • toast notification service (Answer/Decline/Voicemail) + activation      │
│                                                                             │
│  Background connection service  ── ring when closed ──                      │
│    • .NET SignalR client to TelephonyHub (cookie from WebView2)             │
│    • on connect → GET current-incoming-offer to catch an in-flight ring     │
│    • on IncomingCall → toast + ringtone; on CallStateChanged → clear/stop   │
│    • Decline/Voicemail → invoke Reject/Voicemail on the hub directly        │
│                                                                             │
│  Phone window (WebView2 → https://{domain}/softphone?host=extension)         │
│    • the real soft phone; the WebRTC media leg lives here                   │
│    • collapse-to-compact / expand; survives; Answer navigates to answerCallId│
│                                                                             │
│  Settings + policy                                                          │
│    • effective config = Policy (locked) ▸ User ▸ Default                     │
│    • Settings window (domain, autostart, ringtone) — locked items read-only │
└─────────────────────────────────────────────────────────────────────────────┘
```

Two connections coexist by design (same as the extension): the background's (always on, for
ringing) and the page's (when open, for the actual call). Both are in the same per-user hub
group. **Suppress the toast when the phone window is open and focused** (the page shows its
own incoming UI) to avoid double UI.

---

## 4. Repository layout

```
CrestApps.SoftPhone.Windows/
  CrestApps.SoftPhone.Windows.sln
  src/
    SoftPhone.App/                 # WPF app (tray, windows, WebView2 host)
      App.xaml(.cs)                # startup, single-instance, DI, tray
      TrayIcon.cs                  # H.NotifyIcon menu + click routing
      PhoneWindow.xaml(.cs)        # WebView2 phone window; collapse/expand
      SettingsWindow.xaml(.cs)     # domain + options; read-only when policy-locked
      IncomingCallWindow.xaml(.cs) # optional in-app popup (mirrors the extension popup)
      Assets/                      # icons (.ico + png), ringtone
    SoftPhone.Core/                # no-UI logic (testable)
      Contract/Types.cs            # Call/CallContext/ExtensionConfig (contract §2)
      Config/ConfigClient.cs       # GET /softphone/extension-config + current-offer
      Hub/TelephonyHubClient.cs    # SignalR .NET client wrapper (start/reject/voicemail)
      Hub/ConnectionHost.cs        # owns connection + ringtone + incoming orchestration
      Cookies/WebViewCookieBridge.cs  # read auth cookie from WebView2 CookieManager
      Settings/SettingsStore.cs    # user settings (per-user, roaming-safe)
      Settings/PolicyProvider.cs   # registry/ADMX policy read + precedence + lock state
      Diagnostics/Diagnostics.cs   # Phase 0 connection self-test + simulate-incoming
    SoftPhone.Notifications/       # toast build + activation-arg parsing (unit-testable)
  packaging/
    SoftPhone.Package/             # MSIX (Windows Application Packaging Project)
      Package.appxmanifest         # identity, capabilities (microphone), startupTask
    policy/
      CrestAppsSoftPhone.admx      # Group Policy template
      en-US/CrestAppsSoftPhone.adml
  tests/
    SoftPhone.Core.Tests/          # xUnit: config mapping, policy precedence, diag
    SoftPhone.Notifications.Tests/ # xUnit: toast args + activation parser
    SoftPhone.UiTests/             # FlaUI/WinAppDriver: tray menu, settings, incoming popup
  .github/workflows/
    ci.yml                         # build + test + package on PR/push
    release.yml                    # tag vX.Y.Z → build, package, sign, submit to Store
    publish-manual.yml             # manual dispatch: submit to Store (version input)
  docs/
    RELEASING.md  PRIVACY.md  ABOUT.md  STORE-LISTING.md  ENTERPRISE-DEPLOYMENT.md
  README.md
  PROJECT-PLAN.md                  # this file
```

## 5. Tech stack

- **.NET 8**, **C#**, **WPF** for the shell (simple, robust for a tray utility; WinUI 3 is an
  acceptable alternative). **WebView2** (`Microsoft.Web.WebView2`) for the phone page.
- **SignalR .NET client** (`Microsoft.AspNetCore.SignalR.Client`) for the background hub
  connection, WebSockets transport + automatic reconnect.
- **Tray:** `H.NotifyIcon.Wpf`. **Toasts:** `Microsoft.Windows.AppNotifications`
  (Windows App SDK) or `CommunityToolkit.WinUI.Notifications` — requires **package identity**
  (develop with the packaging project). **DI/logging:** `Microsoft.Extensions.*`.
- **Packaging:** MSIX via the Windows Application Packaging Project. **Tests:** xUnit +
  **FlaUI** (UI automation). **CI:** GitHub Actions on `windows-latest`.
- Keep `SoftPhone.Core` and `SoftPhone.Notifications` **UI-free** so the important logic is
  unit-testable without a desktop session.

## 6. Enterprise / policy configuration (a first-class requirement)

Corporations must be able to deploy the app and **push configuration** to all agents — at a
minimum the **tenant domain** — and optionally lock it so users can't change it.

**Effective-setting precedence (highest wins):**
1. **Managed policy** (Group Policy / MDM) — when present, the value is used and the UI shows
   it **read-only** with a "Managed by your organization" note.
2. **User setting** (per-user, set in the Settings window).
3. **Packaged default** (usually empty domain).

**Policy sources (`PolicyProvider`):**
- **Registry (ADMX-backed Group Policy / Intune):**
  - Machine: `HKLM\SOFTWARE\Policies\CrestApps\SoftPhone`
  - User: `HKCU\SOFTWARE\Policies\CrestApps\SoftPhone`
  - Values: `Domain` (REG_SZ), `AllowUserOverrideDomain` (REG_DWORD 0/1),
    `StartWithWindows` (REG_DWORD), `RingtoneEnabled` (REG_DWORD), etc.
- **Provisioning file (non-GPO shops):** `%ProgramData%\CrestApps\SoftPhone\config.json`
  (machine-wide managed config) — same keys; lower precedence than registry policy, higher
  than user.
- Ship an **ADMX/ADML template** (`packaging/policy/`) so admins configure via the Group
  Policy Editor; Intune can ingest the ADMX or push the same registry values via a
  Settings Catalog / custom OMA-URI profile.

**Deliverables for this area:**
- `PolicyProvider` that merges sources, exposes `EffectiveSettings` + per-setting `IsManaged`.
- Settings window binds to that: managed fields disabled + annotated.
- `docs/ENTERPRISE-DEPLOYMENT.md`: how to deploy the MSIX (Store for Business / Intune /
  sideload) and push config via GPO/Intune, with the ADMX and example registry.
- Unit tests for precedence and lock behavior.

## 7. Runtime flows

- **First run / settings.** If no effective domain, open Settings (or a first-run window).
  Validate the domain (https, reachable). Store the user value in per-user settings; a managed
  domain skips the prompt and locks the field.
- **Open the phone.** Tray click / menu → open or focus the WebView2 phone window pointing at
  `https://{domain}/softphone?host=extension`. First open triggers OrchardCore login inside
  WebView2 if needed.
- **Collapse / expand.** Shrink the window to a compact affordance and restore it; the WebView2
  is **not** re-created, so the call persists. Persist last size/position.
- **Background connection.** On startup (and whenever domain + session exist): fetch
  `extension-config` (cookie), open the SignalR connection to `hubUrl`, then
  `GET current-incoming-offer` to catch an in-flight ring. Handle `IncomingCall` (toast +
  ringtone unless the phone window is focused) and `CallStateChanged` (stop/clear when no
  longer ringing).
- **Incoming toast actions.** **Answer** → open/navigate the phone window to
  `…/softphone?host=extension&answerCallId={callId}` and stop the ring. **Decline** →
  `Reject({CallId})` on the background hub. **Voicemail** → `Voicemail({CallId})`. Clicking the
  toast body = Answer.
- **Screen-pop (optional).** If the page posts a screen-pop URL (via a WebView2
  `WebMessageReceived` bridge on the `/softphone` page only), open it in the user's default
  browser; otherwise the record opens inside the phone window (acceptable).

## 8. Notifications & tray (Windows specifics)

- **Package identity is required** for toasts + `startupTask`, so always run/debug through the
  packaging project (unpackaged builds can't raise app-identity toasts).
- Toasts carry **arguments** (`action=answer&callId=…`); handle **activation** both when the
  app is running and cold-start (register the notification activator). Parse args in
  `SoftPhone.Notifications` (unit-tested).
- **Ringtone** plays from the app (a bundled asset), not the web page, so autoplay policy is
  irrelevant; loop until the call stops or is handled.
- **Autostart** via the MSIX `windows.startupTask` extension (user can toggle; org can force
  via policy).
- **Single instance:** a named mutex / `AppInstance` redirect; a second launch focuses the
  existing tray app.

## 9. Known gotchas (call these out so they aren't rediscovered)

- **Cookie → background connection (spike this FIRST, Phase 0).** Read the tenant session
  cookie from `CoreWebView2.CookieManager` and attach it to the SignalR client
  (`HttpConnectionOptions.Cookies`), or run the background connection inside a **hidden
  WebView2** so cookies are automatic. Verify negotiate + WebSocket authenticate. Fallback:
  short-lived token via `AccessTokenProvider`.
- **Microphone permission (two layers).** Declare the **microphone** capability in the
  appxmanifest; the user must allow it in Windows privacy settings; and handle WebView2
  `PermissionRequested` to grant mic to the tenant origin. Plan for the denied state.
- **Background persistence.** Keep the WebView2/connection alive while hidden; ensure WebView2
  audio isn't suspended when the window is hidden/minimized (don't set it to a suspended state;
  keep the process foregrounded enough or use a hidden host).
- **WebView2 runtime dependency** — evergreen and preinstalled on Windows 11; declare the
  dependency for older Windows 10.
- **Double UI** — suppress the toast when the phone window is focused (the page shows its own
  incoming UI).
- **Store certification** — a WebView2-hosting app is allowed; justify the microphone use and
  that it connects only to the user-configured tenant (same privacy story as the extension).
- **MSIX vs unpackaged** — toasts, `startupTask`, and policy reads assume the packaged app;
  develop against the packaging project from the start.

## 10. Automation (CI/CD), testing & distribution

### 10.1 CI (`ci.yml`, every PR)
- `dotnet restore` → build → **unit tests** (`SoftPhone.Core.Tests`,
  `SoftPhone.Notifications.Tests`) → build the **MSIX** package (unsigned) as an artifact.
- **UI tests** (`SoftPhone.UiTests`, FlaUI) drive the tray menu, Settings window, and the
  incoming-call popup. FlaUI needs an interactive desktop — run on `windows-latest` (works for
  most cases) or a self-hosted interactive runner; keep them a separate, optionally-gated job.

### 10.2 "Ensure it works" — testing strategy (esp. notifications & settings)
- **Notifications:** unit-test the toast **payload/argument builder** and the **activation-arg
  parser** in `SoftPhone.Notifications`. Put a **`--simulate-incoming` dev command** (and a
  diagnostics-mode tray item) that runs the full incoming flow (toast + popup + ring) with a
  fake call — mirrors the extension's "Simulate incoming call" button — so notifications are
  testable without a live call. A documented **manual test matrix** covers real OS toast
  rendering + action activation (running vs cold-start).
- **Settings & policy:** unit-test `SettingsStore` and `PolicyProvider` precedence
  (Policy ▸ User ▸ Default) and lock behavior; FlaUI test that a managed domain renders
  read-only. A registry-fixture test seeds `HKCU\...\Policies\...` and asserts the effective
  value + `IsManaged`.
- **Connection (Phase 0 harness stays in the app):** a **diagnostics "Run connection test"**
  that does config-fetch → SignalR connect → current-offer and reports PASS(cookie)/
  PASS(token)/FAIL — the same idea as the extension's spike page.

### 10.3 Microsoft Store — account & credentials (one-time)
- Enroll in the **Microsoft Partner Center** (Windows developer account; one-time fee).
- Reserve the app name; create the Store listing once; note the **Store/Product IDs**.
- For automated submission, create **Azure AD app credentials** for the **Microsoft Store
  submission API** (Partner Center → tenant association) → secrets `PARTNER_TENANT_ID`,
  `PARTNER_CLIENT_ID`, `PARTNER_CLIENT_SECRET`, and the app's `STORE_APP_ID` (or `SELLER_ID`).
  The workflow uses **StoreBroker** (or the submission REST API) to push the packaged MSIX.
- **Code signing:** Store-signed submissions are re-signed by Microsoft; for sideload/Intune
  distribution provide a signing certificate (secret `SIGNING_PFX_BASE64` + `SIGNING_PFX_PASSWORD`).

### 10.4 Release workflows
- **`release.yml`** — on tag `vX.Y.Z`: stamp the version from the tag into the app + appxmanifest
  (`Version="X.Y.Z.0"`), build + package + sign the MSIX, submit to the Store via
  StoreBroker/API, and cut a GitHub Release with the `.msix`/`.msixbundle` attached.
- **`publish-manual.yml`** — `workflow_dispatch` with a **version input** and a target/flight
  choice, to submit on demand (smoke-test the Store pipeline without a tag). Mirror the
  extension's manual publish workflow.
- **Version is driven by the tag** (like the extension) — do not hand-edit the version;
  inject it at build time. Surface a **build stamp** (version + build time) in the Settings
  window for "which build am I running" confidence.

### 10.5 Distribution summary
- **Individuals:** Microsoft Store (MSIX, auto-updates).
- **Enterprises:** sideload the MSIX via Intune / Store for Business / SCCM, and push config
  (domain, autostart, lock) via **ADMX Group Policy** or **Intune** (registry). See
  `docs/ENTERPRISE-DEPLOYMENT.md`.

## 11. README (ship with the repo)
User/developer-facing (follow the extension's README style — **no phase status, no internal
plan references**): what it is and the ring-when-closed rationale; that it's a companion to a
site running **OrchardCore + the CrestApps Telephony module**
(https://github.com/CrestApps/CrestApps.OrchardCore/) with the **Soft Phone Extension** feature
enabled; install from the Microsoft Store (and sideload for dev); the `{domain}` setting;
enterprise deployment pointer; local build/test; the release process; and the privacy note
(accesses only the configured tenant domain; no data sent to the developer).

## 12. Phased roadmap

1. **Phase 0 — Spike (½–1 day).** Prove the background SignalR connection authenticates using
   the **cookie read from WebView2** (config-fetch + hub connect). Decide cookie vs token
   fallback. Everything depends on this — de-risk it first. *(Needs the tenant's Soft Phone
   Extension feature enabled; a dev tenant is `dialpad-dev.crestapps.online`.)*
2. **Phase 1 — Skeleton.** Solution/projects, single-instance tray app, Settings window
   (domain), open/focus the WebView2 phone window, packaging project with identity.
3. **Phase 2 — Window management.** Collapse/expand, size/position persistence, tray menu.
4. **Phase 3 — Ring when closed.** Background connection service, config-fetch, SignalR connect,
   current-incoming-offer, IncomingCall/CallStateChanged, **toast + ringtone**, Answer via
   `answerCallId`, Decline/Voicemail via hub, double-UI suppression, `--simulate-incoming`.
5. **Phase 4 — Enterprise policy.** `PolicyProvider` (registry/ADMX/provisioning), precedence +
   lock, ADMX/ADML templates, managed-read-only UI, `docs/ENTERPRISE-DEPLOYMENT.md`.
6. **Phase 5 — Automation.** CI (build/test/package), UI tests, MSIX signing, Store release +
   manual-publish workflows, diagnostics/connection test, README + docs.

## 13. Reference
- The browser extension (same contract, same behavior) lives at
  `C:\Code\CrestApps\CrestApps.SoftPhone` — reuse its conventions (tag-driven version, build
  stamp, diagnostics toggle, simulate-incoming, docs structure, manual + tag release workflows).
- Server contract owner: **CrestApps.OrchardCore** Telephony module
  (https://github.com/CrestApps/CrestApps.OrchardCore/).
