# Privacy Policy — CrestApps Soft Phone (Windows)

**The app connects only to the tenant domain you configure. It sends no data to the
developer.**

## What it accesses

- **Your configured tenant domain only.** The app loads `https://{domain}/softphone` in a
  WebView2 window and maintains a background connection to that same tenant's telephony hub.
  It contacts no other servers. It never loads a third-party telephony SDK.
- **Your existing site session.** You sign in with your normal site login inside the app's
  window. The app reuses that session cookie to authenticate its background connection. It
  never sees or stores your password.
- **Microphone.** Used only for the in-call audio of the WebRTC call on the tenant page,
  when you are on a call. Nothing is recorded by the app.

## What it stores (locally, on your device)

- Your chosen tenant domain and preferences (start-at-login, ringtone) in your per-user app
  data.
- The WebView2 browser profile (including your tenant session cookie), in your per-user app
  data, so you stay signed in.
- A local diagnostic log (no message content) to help troubleshoot connection issues.

This data stays on your device. The app has no analytics and no developer-owned backend.

## What it never does

- No data is sent to CrestApps or any third party.
- No tracking, advertising, or profiling.
- No access to any site other than your configured tenant domain.

## Enterprise management

If your organization deploys the app with managed configuration (e.g. a forced domain), that
configuration is read from Windows policy on your device. See
[ENTERPRISE-DEPLOYMENT.md](ENTERPRISE-DEPLOYMENT.md).

## Contact

Questions: privacy@crestapps.com
