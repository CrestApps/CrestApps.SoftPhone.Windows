# About CrestApps Soft Phone (Windows)

CrestApps Soft Phone is the Windows desktop companion to a soft phone provided by the
**CrestApps Telephony module** for **OrchardCore**. It gives agents a native, always-on
experience:

- **Rings when closed.** A background connection to the tenant's telephony hub stays alive
  independently of the phone window, so inbound calls raise a popup + ringtone and can be
  answered, declined, or sent to voicemail even when no window is open.
- **A resilient call window.** The live WebRTC call runs in its own WebView2 window that
  survives you working elsewhere; closing it hides it to the tray so the call keeps going.
- **Your existing login.** You sign in with your normal site session — no separate account.
- **Provider-agnostic.** It speaks only to your tenant's own endpoints and never loads a
  telephony vendor SDK.
- **Enterprise-ready.** Organizations can push and lock configuration via Group Policy or
  Intune.

## How it fits together

- The tenant's `https://{domain}/softphone` page hosts the actual call (WebRTC media leg).
- `GET /softphone/extension-config` tells the app the tenant-aware hub + endpoint URLs.
- The telephony hub delivers `IncomingCall` / `CallStateChanged` / `IncomingCallAnswered` to
  the app's background connection, so it rings even when the phone page is not loaded. Then
  Answer opens the phone at `?answerCallId=…`, and Decline / Voicemail invoke the hub directly.
- When the phone page is loaded, the page and the app hand the incoming call over through the
  WebView2 message channel (`src/SoftPhone.Core/Contract/HostBridge.cs`). The page sends the
  ringing call with its queue and matched records; the app's popup shows them, and the page
  hides its own incoming modal only after the app confirms the popup is on screen. If the app
  does not confirm within 2 seconds, or the popup closes without an answer, the page shows its
  modal again. Answer, Decline, and Voicemail in the popup run in the page (it holds the call
  audio and the offer), so answering never reloads the page. The popup closes when the page
  reports that the call stopped ringing: answered, declined, expired, revoked, or hung up.

All of this is served by the
[CrestApps.OrchardCore](https://github.com/CrestApps/CrestApps.OrchardCore/) Telephony
module — the app requires its **Soft Phone Extension** feature to be enabled on the tenant.

## Support

- Issues: this repository's issue tracker.
- Email: support@crestapps.com
