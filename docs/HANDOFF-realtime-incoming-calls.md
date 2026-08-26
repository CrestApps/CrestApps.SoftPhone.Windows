# Handoff — real-time incoming-call popup for QUEUE calls

Self-contained context for a fresh session. The Windows Soft Phone app is otherwise complete
and shipping; this is the one open issue. The fix is most likely **server-side**, but read
"Next steps" — confirm with a log first, then implement in the right place.

## The problem
For **inbound queue / contact-center calls**, the Windows app (and the browser extension's
background connection) never show the incoming popup. The ring and the rich "INCOMING CALL"
UI appear only inside the `/softphone` page (its own connection). Direct-to-agent DID calls
are unaffected — those do fire the popup.

## Repos
- **Windows app** (primary): `C:\Code\CrestApps\CrestApps.SoftPhone.Windows` → GitHub
  `github.com/CrestApps/CrestApps.SoftPhone.Windows`, branch `main`. Latest tag **v0.1.8**. .NET 10.
- **Server**: `C:\Code\CrestApps\CrestApps.OrchardCore` (OrchardCore Telephony + ContactCenter).
- **Browser extension** (reference client, same server contract): `C:\Code\CrestApps\CrestApps.SoftPhone`.
- Dev tenant: `https://dialpad-dev.crestapps.online` (uses queues; user is `malhayek`).

## Root cause — PROVEN
- `TelephonyHub.IncomingCall` is dispatched **only** by `DialpadDirectInboundCallRouter`
  (`src/Modules/CrestApps.OrchardCore.DialPad/Services/DialpadDirectInboundCallRouter.cs:82`) —
  i.e. **direct inbound** only.
- **Queue** inbound (`AgentReserved`) goes through
  `src/Modules/CrestApps.OrchardCore.ContactCenter/Handlers/ContactCenterRealTimeEventHandler.cs`
  → `BroadcastOfferReceivedAsync` → `ContactCenterRealTimeNotifier.NotifyOfferReceivedAsync`
  → **`ContactCenterHub.OfferReceived` only**. It never calls `IIncomingCallDispatcher`, so
  **`IncomingCall` never fires** for queue calls.
- Both `IncomingCall` and `CallStateChanged` are sent to the **same** per-user group
  `TenantSignalRGroupName.ForUser(tenant, userId)`; `TelephonyHub.OnConnectedAsync` adds each
  authorized connection to it. The Windows app **is** in the group (it receives
  `CallStateChanged`), so this is NOT a group/auth/binding problem — `IncomingCall` is simply
  never dispatched for queue calls.

## What was RULED OUT (don't redo)
- **Client argument binding**: disproven by `tests/SoftPhone.Core.Tests/SignalRIncomingCallBindingTests.cs`
  — a real in-process SignalR hub pushes `IncomingCall(call, richContext)` and the strict
  `On<Call, CallContext>` handler receives it fine. (v0.1.8 briefly added a loose-binding
  workaround; it was reverted — do not reintroduce it.)
- **Auth/connection**: fine (Connected, cookies read, config OK). The WebSocket does drop
  sometimes ("remote party closed … without completing the close handshake"); infinite
  reconnect is already configured.

## Current Windows-app behavior (v0.1.8)
- Listens for `IncomingCall` + `CallStateChanged` on `TelephonyHub`. Receives `CallStateChanged`,
  never `IncomingCall` for queue calls (per root cause).
- Fallback poll: every 2.5s it calls `ConnectionHost.CheckCurrentOfferAsync` →
  `GET current-incoming-offer` and raises `IncomingCall` if a call is ringing
  (`App.StartOfferPolling`). **User reported v0.1.8 still showed no popup**, so the poll didn't
  surface it either — this must be explained (see step 1).
- SignalR client warnings/errors are forwarded to the app log (lines starting `SignalR[`).
- Log: `%LocalAppData%\CrestApps\SoftPhone\logs\app.log`. Settings: `%APPDATA%\CrestApps\SoftPhone\settings.json`.

## Next steps
1. **Get the v0.1.8 log during an inbound queue call** (user runs on their machine):
   `Get-Content "$env:LOCALAPPDATA\CrestApps\SoftPhone\logs\app.log" -Tail 60`.
   Look for: `Background: hub IncomingCall …` (expected ABSENT), `Background: current-offer ringing …`
   vs `nothing ringing` (does the poll see it?), `SignalR[Warning] …`, and `Connection status`.
   - If `current-offer ringing` never appears during a real ring, the current-offer endpoint
     (`AgentSoftPhoneEndpoints` / `PendingIncomingCallOfferService.GetForUserAsync`) isn't
     populated for queue offers, or `config.currentIncomingOfferUrl` isn't the queue-offer
     endpoint — investigate that too.

2. **Preferred fix (server)**: in the queue-offer path (`AgentReserved` →
   `BroadcastOfferReceivedAsync`), ALSO dispatch the soft-phone ring via
   `IIncomingCallDispatcher.DispatchAsync(agentUserId, telephonyCall)`. This is the designed
   soft-phone notification (it runs `ContactCenterIncomingCallContextProvider` to attach the
   matched-customer cards). It lights up the page, the **extension**, and the Windows app with
   **no client changes**.
   - Need inside that handler: the agent's userId and enough to build a `TelephonyCall`
     (CallId/From/To/Direction/ProviderName). Read `BroadcastOfferReceivedAsync` (~line 176–230),
     the `AgentOfferNotification` model, and `DefaultIncomingCallDispatcher`.
   - **MUST verify no double incoming UI on the `/softphone` page** (it already handles
     `OfferReceived`; adding `IncomingCall` could double up). Check the page's client JS and
     dedupe by `CallId` if necessary. **Validate via the browser extension first**, then the
     Windows app.
   - Confirm whether the extension currently misses queue offers too (it likely does) — this
     fix should benefit it as well.

3. **Alternative (client-only, less preferred)**: have the Windows app also connect to
   `ContactCenterHub` and handle `OfferReceived` (map `AgentOfferNotification` → Call/CallContext).
   Downsides: needs the ContactCenterHub URL (not in `extension-config` today), duplicates the
   contact-center client, and diverges from the extension. Only do this if the server can't change.

## Key Windows-app files
- `src/SoftPhone.Core/Hub/ConnectionHost.cs` — orchestration; `CheckCurrentOfferAsync` (poll); events; logging.
- `src/SoftPhone.Core/Hub/TelephonyHubClient.cs` — SignalR client (`On` IncomingCall/CallStateChanged, infinite reconnect, `CallbackLoggerProvider`).
- `src/SoftPhone.App/App.xaml.cs` — wiring; `StartOfferPolling` (2.5s); `PhoneWentBackground`; connection status.
- `src/SoftPhone.App/IncomingCallCoordinator.cs` — ringtone + popup + focus/minimize suppression + `OnNoActiveOffer`.
- `tests/SoftPhone.Core.Tests/SignalRIncomingCallBindingTests.cs` — the binding proof.

## Key server files
- `.../Telephony/Services/DefaultIncomingCallDispatcher.cs` — `IncomingCall` → `ForUser` group.
- `.../Telephony/Hubs/TelephonyHub.cs` — hub; `OnConnectedAsync` group add.
- `.../DialPad/Services/DialpadDirectInboundCallRouter.cs` — the only `IncomingCall` caller (direct inbound).
- `.../ContactCenter/Handlers/ContactCenterRealTimeEventHandler.cs` — `AgentReserved` → `OfferReceived` (the gap to fix).
- `.../ContactCenter/Services/ContactCenterRealTimeNotifier.cs` — `OfferReceived` sender.
- `.../ContactCenter/Endpoints/AgentSoftPhoneEndpoints.cs` + `Services/PendingIncomingCallOfferService.cs` — the current-incoming-offer endpoint (poll target).

## Build / test / release (Windows app)
- Build: `dotnet build CrestApps.SoftPhone.Windows.sln -c Debug`. Tests: `dotnet test` each of
  `tests/SoftPhone.Core.Tests`, `SoftPhone.Notifications.Tests`, `SoftPhone.UiTests` (FlaUI, needs desktop).
- Release: push tag `vX.Y.Z` → `release.yml` builds the **signed installer + portable zip** and
  attaches them to a GitHub Release. Or Actions → **Release** → Run workflow (manual, version input).
  MSIX is gated behind `STORE_APP_ID` (not set yet). Self-signed cert signs builds
  (`SIGNING_PFX_*` in the `production` environment). Poll a release run via the GitHub API.
- Dev flags: `--tray`, `--settings`, `--simulate-incoming` (simulate bypasses the hub → tests popup UI only).

## Watch out
- Don't reintroduce the loose-binding workaround (binding is proven fine).
- `--simulate-incoming` won't reproduce this bug (it injects a fake call locally). Only a **real
  inbound queue call** to the dev tenant reproduces it.
- The user strongly prefers real-time over polling; treat polling as a backup only.
