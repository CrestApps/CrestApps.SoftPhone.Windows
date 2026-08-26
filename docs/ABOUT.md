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
- The telephony hub delivers `IncomingCall` / `CallStateChanged`; the app answers by opening
  the phone to `?answerCallId=…`, and declines/voicemails by invoking the hub directly.

All of this is served by the
[CrestApps.OrchardCore](https://github.com/CrestApps/CrestApps.OrchardCore/) Telephony
module — the app requires its **Soft Phone Extension** feature to be enabled on the tenant.

## Support

- Issues: this repository's issue tracker.
- Email: support@crestapps.com
