# Microsoft Store Listing

Copy for the Partner Center listing. Keep in sync with the app.

## Name

Soft Phone

## Short description (≤ 100 chars)

Always-on desktop soft phone that rings even when closed — for OrchardCore + CrestApps Telephony.

## Description

Soft Phone is the Windows desktop companion to your organization's soft phone. It keeps a
background connection to your telephony hub alive so inbound calls ring — with a popup and
ringtone — even when the phone window is closed. Answer, decline, or send to voicemail right
from the incoming popup. Your live call runs in its own resilient window while you work
anywhere else.

- Rings when closed — never miss a queued call.
- Answer, decline, or voicemail from the incoming popup.
- Signs in with your normal site login; no separate password.
- Connects only to your organization's configured domain — no third-party telephony SDK,
  no data sent to the developer.
- Starts with Windows and lives quietly in the system tray.
- Enterprise-ready: administrators can push and lock configuration via Group Policy or Intune.

**Requires** a website running OrchardCore with the CrestApps Telephony module and its
**Soft Phone Extension** feature enabled. On first run you enter your organization's soft
phone domain and sign in.

## Features (bullets)

- Background ring-when-closed
- Incoming popup: Answer / Decline / Voicemail
- Resilient WebRTC call window
- System tray + start at sign-in
- Connection diagnostics / self-test
- Group Policy / Intune managed configuration

## Category

Business / Productivity

## Search terms

soft phone, softphone, VoIP, telephony, call center, contact center, OrchardCore, CrestApps, WebRTC

## Privacy policy URL

https://crestapps.com/soft-phone/privacy  (mirror of docs/PRIVACY.md)

## Notes for certification

- The app hosts the user's own first-party tenant page in WebView2 and requests microphone
  only for the in-call audio of that page. It connects solely to the user-configured tenant
  domain. No data is sent to the developer.
